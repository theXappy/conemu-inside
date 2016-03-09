﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

using ConEmu.WinForms.Util;

using JetBrains.Annotations;

using Microsoft.Build.Utilities;

using Timer = System.Windows.Forms.Timer;

namespace ConEmu.WinForms
{
	/// <summary>
	///     <para>A single session of the console emulator running a console process. Each console process execution in the control spawns a new console emulator and a new session.</para>
	/// <para>When the console emulator starts, a console view appears in the control. The console process starts running in it immediately. When the console process terminates, the console emulator might or might not be closed, depending on the settings. After the console emulator closes, the control stops viewing the console, and this session ends.</para>
	/// </summary>
	public class ConEmuSession
	{
		/// <summary>
		/// A service option. Whether to load the ConEmu helper DLL in-process to communicate with ConEmu (<c>True</c>, new mode), or start a new helper process to send each command (<c>False</c>, legacy mode).
		/// </summary>
		private static readonly bool IsExecutingGuiMacrosInProcess = true;

		/// <summary>
		/// Non-NULL if we've requested ANSI log from ConEmu and are listening to it.
		/// </summary>
		[CanBeNull]
		private readonly AnsiLog _ansilog;

		/// <summary>
		/// Per-session temp files, like the startup options for ConEmu and ANSI log cache.
		/// </summary>
		[NotNull]
		private readonly DirectoryInfo _dirTempWorkingFolder;

		/// <summary>
		/// Sends commands to the ConEmu instance and gets info from it.
		/// </summary>
		[NotNull]
		private readonly GuiMacroExecutor _guiMacroExecutor;

		/// <summary>
		/// Executed to process disposal.
		/// </summary>
		[NotNull]
		private readonly List<Action> _lifetime = new List<Action>();

		/// <summary>
		/// The exit code of the console process, if it has already exited. <c>Null</c>, if the console process is still running within the console emulator.
		/// </summary>
		private int? _nConsoleProcessExitCode2;

		/// <summary>
		/// The ConEmu process, even after it exits.
		/// </summary>
		[NotNull]
		private readonly Process _process;

		/// <summary>
		/// Stores the main thread scheduler, so that all state properties were only changed on this thread.
		/// </summary>
		[NotNull]
		private readonly TaskScheduler _schedulerSta = TaskScheduler.FromCurrentSynchronizationContext();

		/// <summary>
		/// The original parameters for this session; sealed, so they can't change after the session is run.
		/// </summary>
		[NotNull]
		private readonly ConEmuStartInfo _startinfo;

		/// <summary>
		/// Task-based notification of the console emulator closing.
		/// </summary>
		[NotNull]
		private readonly TaskCompletionSource<Missing> _taskConsoleEmulatorClosed = new TaskCompletionSource<Missing>();

		/// <summary>
		/// Task-based notification of the console process exiting.
		/// </summary>
		[NotNull]
		private readonly TaskCompletionSource<ProcessExitedEventArgs> _taskConsoleProcessExit = new TaskCompletionSource<ProcessExitedEventArgs>();

		public ConEmuSession([NotNull] ConEmuStartInfo startinfo, [NotNull] HostContext hostcontext)
		{
			if(startinfo == null)
				throw new ArgumentNullException(nameof(startinfo));
			if(hostcontext == null)
				throw new ArgumentNullException(nameof(hostcontext));
			if(string.IsNullOrEmpty(startinfo.ConsoleCommandLine))
				throw new InvalidOperationException($"Cannot start a new console process for command line “{startinfo.ConsoleCommandLine}” because it's either NULL, or empty, or whitespace.");

			_startinfo = startinfo;
			startinfo.MarkAsUsedUp();

			// Directory for working files, +cleanup
			_dirTempWorkingFolder = Init_TempWorkingFolder();

			// Events wiring: make sure sinks pre-installed with start-info also get notified
			Init_WireEvents(startinfo);

			// Should feed ANSI log?
			if(startinfo.IsReadingAnsiStream)
				_ansilog = Init_AnsiLog(startinfo);

			// Cmdline
			CommandLineBuilder cmdl = Init_MakeConEmuCommandLine(startinfo, hostcontext, _ansilog, _dirTempWorkingFolder);

			// Start ConEmu
			// If it fails, lifetime will be terminated; from them on, termination will be bound to ConEmu process exit
			_process = Init_StartConEmu(startinfo, cmdl);

			// GuiMacro executor
			_guiMacroExecutor = new GuiMacroExecutor(startinfo.ConEmuConsoleServerExecutablePath);
			_lifetime.Add(() => ((IDisposable)_guiMacroExecutor).Dispose());

			// Monitor payload process
			Init_PayloadProcessMonitoring();
		}

		/// <summary>
		/// <para>Gets whether the console process has already exited (see <see cref="PayloadExited" />). The console emulator view might have closed as well, but might have not (see <see cref="ConEmuStartInfo.WhenPayloadProcessExits"/>).</para>
		/// <para>This state only changes on the main thread.</para>
		/// </summary>
		public bool IsConsoleProcessExited => _nConsoleProcessExitCode2.HasValue;

		/// <summary>
		/// Gets the start info with which this session has been started.
		/// </summary>
		[NotNull]
		public ConEmuStartInfo StartInfo
		{
			get
			{
				return _startinfo;
			}
		}

		/// <summary>
		/// Starts construction of the ConEmu GUI Macro, see http://conemu.github.io/en/GuiMacro.html .
		/// </summary>
		[Pure]
		public GuiMacroBuilder BeginGuiMacro([NotNull] string sMacroName)
		{
			if(sMacroName == null)
				throw new ArgumentNullException(nameof(sMacroName));

			return new GuiMacroBuilder(this, sMacroName, Enumerable.Empty<string>());
		}

		/// <summary>
		/// Executes a ConEmu GUI Macro on the active console, see http://conemu.github.io/en/GuiMacro.html .
		/// </summary>
		/// <param name="macrotext">The full macro command, see http://conemu.github.io/en/GuiMacro.html .</param>
		public Task<GuiMacroResult> ExecuteGuiMacroTextAsync([NotNull] string macrotext)
		{
			if(macrotext == null)
				throw new ArgumentNullException(nameof(macrotext));

			Process processConEmu = _process;
			if(processConEmu == null)
				throw new InvalidOperationException("Cannot execute a macro because the console process is not running at the moment.");

			return IsExecutingGuiMacrosInProcess ? _guiMacroExecutor.ExecuteInProcessAsync(processConEmu.Id, macrotext) : _guiMacroExecutor.ExecuteViaExtenderProcessAsync(macrotext, processConEmu.Id, _startinfo.ConEmuConsoleExtenderExecutablePath);
		}

		/// <summary>
		/// Executes a ConEmu GUI Macro on the active console, see http://conemu.github.io/en/GuiMacro.html , synchronously.
		/// </summary>
		/// <param name="macrotext">The full macro command, see http://conemu.github.io/en/GuiMacro.html .</param>
		public GuiMacroResult ExecuteGuiMacroTextSync([NotNull] string macrotext)
		{
			if(macrotext == null)
				throw new ArgumentNullException(nameof(macrotext));

			Task<GuiMacroResult> task = ExecuteGuiMacroTextAsync(macrotext);

			// No meaningful message pump on an MTA thread by contract, so can just do a blocking wait
			if(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
				return task.Result;

			// On an STA thread we should be pumping
			bool isThru = false;
			task.ContinueWith(t => isThru = true);

			while(!isThru)
			{
				Application.DoEvents();
				Thread.Sleep(10);
			}
			return task.Result;
		}

		/// <summary>
		/// <para>Gets the exit code of the console process, if <see cref="IsConsoleProcessExited">it has already exited</see>. Throws an exception if it has not.</para>
		/// <para>This state only changes on the main thread.</para>
		/// </summary>
		public int GetConsoleProcessExitCode()
		{
			int? nCode = _nConsoleProcessExitCode2;
			if(!nCode.HasValue)
				throw new InvalidOperationException("The exit code is not available yet because the console process is still running.");
			return nCode.Value;
		}

		/// <summary>
		/// Kills the whole console emulator process if it is running. This also terminates the console emulator window.	// TODO: kill payload process only when we know its pid
		/// </summary>
		public void KillConsoleEmulator()
		{
			try
			{
				if(!_process.HasExited)
					BeginGuiMacro("Close").WithParam(1 /*terminate active process*/).WithParam(1 /*without confirmation*/).ExecuteSync();
			}
			catch(Exception)
			{
				// Might be a race, so in between HasExited and Kill state could change, ignore possible errors here
			}
		}

		/// <summary>
		///     <para>Kills the console payload process, if it's running.</para>
		///     <para>This does not necessarily kill the console emulator process which displays the console window, but it might also close if <see cref="ConEmuStartInfo.WhenPayloadProcessExits" /> says so.</para>
		/// </summary>
		[NotNull]
		public Task<bool> KillConsolePayloadProcessAsync()
		{
			try
			{
				if((!_process.HasExited) && (!_nConsoleProcessExitCode2.HasValue))
				{
					return GetInfoRoot.QueryAsync(this).ContinueWith(task =>
					{
						if(task.Status != TaskStatus.RanToCompletion)
							return false;
						if(!task.Result.Pid.HasValue)
							return false;
						try
						{
							Process.GetProcessById((int)task.Result.Pid.Value).Kill();
						}
						catch(Exception)
						{
							// Most likely, has already exited
						}
						return true;
					});
				}
			}
			catch(Exception)
			{
				// Might be a race, so in between HasExited and Kill state could change, ignore possible errors here
			}
			var tcs = new TaskCompletionSource<bool>();
			tcs.SetResult(true);
			return tcs.Task;
		}

		/// <summary>
		///     <para>Sends the Control+Break signal to the payload console process, which will most likely abort it.</para>
		///     <para>Unlike <see cref="KillConsolePayloadProcessAsync" />, this is a soft signal which might be processed by the console process for a graceful shutdown, or ignored altogether.</para>
		/// </summary>
		public Task SendControlBreakAsync()
		{
			try
			{
				if(!_process.HasExited)
					return BeginGuiMacro("Break").WithParam(1 /* Ctrl+Break */).ExecuteAsync();
			}
			catch(Exception)
			{
				// Might be a race, so in between HasExited and Kill state could change, ignore possible errors here
			}
			return TaskHelpers.CompletedTask;
		}

		/// <summary>
		///     <para>Sends the Control+C signal to the payload console process, which will most likely abort it.</para>
		///     <para>Unlike <see cref="KillConsolePayloadProcessAsync" />, this is a soft signal which might be processed by the console process for a graceful shutdown, or ignored altogether.</para>
		/// </summary>
		public Task SendControlCAsync()
		{
			try
			{
				if(!_process.HasExited)
					return BeginGuiMacro("Break").WithParam(0 /* Ctrl+C */).ExecuteAsync();
			}
			catch(Exception)
			{
				// Might be a race, so in between HasExited and Kill state could change, ignore possible errors here
			}
			return TaskHelpers.CompletedTask;
		}

		/// <summary>
		///     <para>Waits until the console emulator process exits and stops rendering the terminal view, or completes immediately if it has already exited.</para>
		/// </summary>
		[NotNull]
		public Task WaitForConsoleEmulatorExitAsync()
		{
			return _taskConsoleEmulatorClosed.Task;
		}

		/// <summary>
		/// Waits for the payload console command to exit within the terminal, or completes immediately if it has already exited. If not <see cref="WhenPayloadProcessExits.CloseTerminal" />, the terminal stays, otherwise it closes also.
		/// </summary>
		[NotNull]
		public Task<ProcessExitedEventArgs> WaitForConsolePayloadExitAsync()
		{
			return _taskConsoleProcessExit.Task;
		}

		/// <summary>
		///     <para>Writes text to the console input, as if it's been typed by user on the keyboard.</para>
		///     <para>Whether this will be visible (=echoed) on screen is up to the running console process.</para>
		/// </summary>
		public Task WriteInputText([NotNull] string text)
		{
			if(text == null)
				throw new ArgumentNullException(nameof(text));
			if(text.Length == 0)
				return TaskHelpers.CompletedTask;

			return BeginGuiMacro("Paste").WithParam(2).WithParam(text).ExecuteAsync();
		}

		/// <summary>
		///     <para>Writes text to the console output, as if the current running console process has written it to stdout.</para>
		///     <para>Use with caution, as this might interfere with console process output in an unpredictable manner.</para>
		/// </summary>
		public Task WriteOutputText([NotNull] string text)
		{
			if(text == null)
				throw new ArgumentNullException(nameof(text));
			if(text.Length == 0)
				return TaskHelpers.CompletedTask;

			return BeginGuiMacro("Write").WithParam(text).ExecuteAsync();
		}

		/// <summary>
		///     <para>Fires when the console process writes into its output or error stream. Gets a chunk of the raw ANSI stream contents.</para>
		///     <para>For processes which write immediately on startup, this event might fire some chunks before you sink it. To get notified reliably, use <see cref="ConEmuStartInfo.AnsiStreamChunkReceivedEventSink" />.</para>
		///     <para>To enable sinking this event, you must have <see cref="ConEmuStartInfo.IsReadingAnsiStream" /> set to <c>True</c> before starting the console process.</para>
		///     <para>If you're reading the ANSI log with <see cref="AnsiStreamChunkReceived" />, it's guaranteed that all the events for the log will be fired before <see cref="PayloadExited" />, and there will be no events afterwards.</para>
		/// </summary>
		[SuppressMessage("ReSharper", "DelegateSubtraction")]
		public event EventHandler<AnsiStreamChunkEventArgs> AnsiStreamChunkReceived
		{
			add
			{
				if(_ansilog == null)
					throw new InvalidOperationException("You cannot receive the ANSI stream data because the console process has not been set up to read the ANSI stream before running; set ConEmuStartInfo::IsReadingAnsiStream to True before starting the process.");
				_ansilog.AnsiStreamChunkReceived += value;
			}
			remove
			{
				if(_ansilog == null)
					throw new InvalidOperationException("You cannot receive the ANSI stream data because the console process has not been set up to read the ANSI stream before running; set ConEmuStartInfo::IsReadingAnsiStream to True before starting the process.");
				_ansilog.AnsiStreamChunkReceived -= value;
			}
		}

		/// <summary>
		///     <para>Fires when the console emulator process exits and stops rendering the terminal view. Note that the root command might have had stopped running long before this moment if not <see cref="WhenPayloadProcessExits.CloseTerminal" /> prevents terminating the terminal view immediately.</para>
		///     <para>For short-lived processes, this event might fire before you sink it. To get notified reliably, use <see cref="WaitForConsoleEmulatorExitAsync" /> or <see cref="ConEmuStartInfo.ConsoleEmulatorExitedEventSink" />.</para>
		/// </summary>
		public event EventHandler ConsoleEmulatorExited;

		[NotNull]
		private AnsiLog Init_AnsiLog([NotNull] ConEmuStartInfo startinfo)
		{
			var ansilog = new AnsiLog(_dirTempWorkingFolder);
			_lifetime.Add(() => ansilog.Dispose());
			if(startinfo.AnsiStreamChunkReceivedEventSink != null)
				ansilog.AnsiStreamChunkReceived += startinfo.AnsiStreamChunkReceivedEventSink;

			// Do the pumping periodically (TODO: take this to async?.. but would like to keep the final evt on the home thread, unless we go to tasks)
			// TODO: if ConEmu writes to a pipe, we might be getting events when more data comes to the pipe rather than poll it by timer
			var timer = new Timer() {Interval = (int)TimeSpan.FromSeconds(.1).TotalMilliseconds, Enabled = true};
			timer.Tick += delegate { ansilog.PumpStream(); };
			_lifetime.Add(() => timer.Dispose());

			return ansilog;
		}

		[NotNull]
		private static unsafe CommandLineBuilder Init_MakeConEmuCommandLine([NotNull] ConEmuStartInfo startinfo, [NotNull] HostContext hostcontext, [CanBeNull] AnsiLog ansilog, [NotNull] DirectoryInfo dirLocalTempRoot)
		{
			if(startinfo == null)
				throw new ArgumentNullException(nameof(startinfo));
			if(hostcontext == null)
				throw new ArgumentNullException(nameof(hostcontext));

			var cmdl = new CommandLineBuilder();

			// This sets up hosting of ConEmu in our control
			cmdl.AppendSwitch("-InsideWnd");
			cmdl.AppendFileNameIfNotNull("0x" + ((ulong)hostcontext.HWndParent).ToString("X"));

			// Don't use keyboard hooks in ConEmu when embedded
			cmdl.AppendSwitch("-NoKeyHooks");

			// Basic settings, like fonts and hidden tab bar
			// Plus some of the properties on this class
			cmdl.AppendSwitch("-LoadCfgFile");
			cmdl.AppendFileNameIfNotNull(Init_MakeConEmuCommandLine_EmitConfigFile(dirLocalTempRoot, startinfo, hostcontext));

			if(!string.IsNullOrEmpty(startinfo.StartupDirectory))
			{
				cmdl.AppendSwitch("-Dir");
				cmdl.AppendFileNameIfNotNull(startinfo.StartupDirectory);
			}

			// ANSI Log file
			if(ansilog != null)
			{
				cmdl.AppendSwitch("-AnsiLog");
				cmdl.AppendFileNameIfNotNull(ansilog.Directory.FullName);
			}
			if(dirLocalTempRoot == null)
				throw new ArgumentNullException(nameof(dirLocalTempRoot));

			// This one MUST be the last switch
			cmdl.AppendSwitch("-cmd");

			// Console mode command
			// NOTE: if placed AFTER the payload command line, otherwise somehow conemu hooks won't fetch the switch out of the cmdline, e.g. with some complicated git fetch/push cmdline syntax which has a lot of colons inside on itself
			string sConsoleExitMode;
			switch(startinfo.WhenPayloadProcessExits)
			{
			case WhenPayloadProcessExits.CloseTerminal:
				sConsoleExitMode = "n";
				break;
			case WhenPayloadProcessExits.KeepTerminal:
				sConsoleExitMode = "c0";
				break;
			case WhenPayloadProcessExits.KeepTerminalAndShowMessage:
				sConsoleExitMode = "c";
				break;
			default:
				throw new ArgumentOutOfRangeException("ConEmuStartInfo" + "::" + "WhenPayloadProcessExits", startinfo.WhenPayloadProcessExits, "This is not a valid enum value.");
			}
			cmdl.AppendSwitchIfNotNull("-cur_console:", $"{(startinfo.IsElevated ? "a" : "")}{sConsoleExitMode}");

			// And the shell command line itself
			cmdl.AppendSwitch(startinfo.ConsoleCommandLine);

			return cmdl;
		}

		private static string Init_MakeConEmuCommandLine_EmitConfigFile([NotNull] DirectoryInfo dirForConfigFile, [NotNull] ConEmuStartInfo startinfo, [NotNull] HostContext hostcontext)
		{
			if(dirForConfigFile == null)
				throw new ArgumentNullException(nameof(dirForConfigFile));
			if(startinfo == null)
				throw new ArgumentNullException(nameof(startinfo));
			if(hostcontext == null)
				throw new ArgumentNullException(nameof(hostcontext));
			// Load default template
			var xmldoc = new XmlDocument();
			xmldoc.Load(new MemoryStream(Resources.ConEmuSettingsTemplate));
			XmlNode xmlSettings = xmldoc.SelectSingleNode("/key[@name='Software']/key[@name='ConEmu']/key[@name='.Vanilla']");
			if(xmlSettings == null)
				throw new InvalidOperationException("Unexpected mismatch in XML resource structure.");

			// Apply settings from properties
			{
				string keyname = "StatusBar.Show";
				var xmlElem = ((XmlElement)(xmlSettings.SelectSingleNode($"value[@name='{keyname}']") ?? xmlSettings.AppendChild(xmldoc.CreateElement("value"))));
				xmlElem.SetAttribute("name", keyname);
				xmlElem.SetAttribute("type", "hex");
				xmlElem.SetAttribute("data", (hostcontext.IsStatusbarVisibleInitial ? 1 : 0).ToString());
			}

			// Environment variables
			if((startinfo.EnumEnv().Any()) || (startinfo.IsEchoingConsoleCommandLine) || (startinfo.GreetingText.Length > 0))
			{
				string keyname = "EnvironmentSet";
				var xmlElem = ((XmlElement)(xmlSettings.SelectSingleNode($"value[@name='{keyname}']") ?? xmlSettings.AppendChild(xmldoc.CreateElement("value"))));
				xmlElem.SetAttribute("name", keyname);
				xmlElem.SetAttribute("type", "multi");
				foreach(string key in startinfo.EnumEnv())
				{
					XmlElement xmlLine;
					xmlElem.AppendChild(xmlLine = xmldoc.CreateElement("line"));
					xmlLine.SetAttribute("data", $"set {key}={startinfo.GetEnv(key)}");
				}

				// Echo the custom greeting text
				if(startinfo.GreetingText.Length > 0)
				{
					// Echo each line separately
					List<string> lines = Regex.Split(startinfo.GreetingText, @"\r\n|\n|\r").ToList();
					if((lines.Any()) && (lines.Last().Length == 0)) // Newline handling, as declared
						lines.RemoveAt(lines.Count - 1);
					foreach(string line in lines)
					{
						XmlElement xmlLine;
						xmlElem.AppendChild(xmlLine = xmldoc.CreateElement("line"));
						xmlLine.SetAttribute("data", $"echo {Init_MakeConEmuCommandLine_EmitConfigFile_EscapeEchoText(line)}");
					}
				}

				// To echo the cmdline, add an echo command to the env-init session
				if(startinfo.IsEchoingConsoleCommandLine)
				{
					XmlElement xmlLine;
					xmlElem.AppendChild(xmlLine = xmldoc.CreateElement("line"));
					xmlLine.SetAttribute("data", $"echo {Init_MakeConEmuCommandLine_EmitConfigFile_EscapeEchoText(startinfo.ConsoleCommandLine)}");
				}
			}

			// Write out to temp location
			dirForConfigFile.Create();
			string sConfigFile = Path.Combine(dirForConfigFile.FullName, "Config.Xml");
			xmldoc.Save(sConfigFile);

			return sConfigFile;
		}

		/// <summary>
		/// Applies escaping so that (1) it went as a single argument into the ConEmu's <c>NextArg</c> function; (2) its special chars were escaped according to the ConEmu's <c>DoOutput</c> function which implements this echo.
		/// </summary>
		private static string Init_MakeConEmuCommandLine_EmitConfigFile_EscapeEchoText([NotNull] string text)
		{
			if(text == null)
				throw new ArgumentNullException(nameof(text));

			var sb = new StringBuilder(text.Length + 2);

			// We'd always quote the arg; no harm, and works better with an empty string
			sb.Append('"');

			foreach(char ch in text)
			{
				switch(ch)
				{
				case '"':
					sb.Append('"').Append('"'); // Quotes are doubled in this format
					break;
				case '^':
					sb.Append("^^");
					break;
				case '\r':
					sb.Append("^R");
					break;
				case '\n':
					sb.Append("^N");
					break;
				case '\t':
					sb.Append("^T");
					break;
				case '\x7':
					sb.Append("^A");
					break;
				case '\b':
					sb.Append("^B");
					break;
				case '[':
					sb.Append("^E");
					break;
				default:
					sb.Append(ch);
					break;
				}
			}

			// Close arg quoting
			sb.Append('"');

			return sb.ToString();
		}

		/// <summary>
		/// Watches for the status of the payload process to fetch its exitcode when done and notify user of that.
		/// </summary>
		private void Init_PayloadProcessMonitoring()
		{
			// When the payload process exits, use its exit code
			Action<Task<int?>> λExited = task =>
			{
				if(!task.Result.HasValue) // Means the wait were aborted, e.g. ConEmu has been shut down and we processed that on the main thread
					return;
				TryFirePayloadExited(task.Result.Value);
			};

			// Detect when this happens
			Init_PayloadProcessMonitoring_WaitForExitCodeAsync().ContinueWith(λExited, _schedulerSta /* to the main thread*/);
		}

		private async Task<int?> Init_PayloadProcessMonitoring_WaitForExitCodeAsync()
		{
			// Async-loop retries for getting the root payload process to await its exit
			for(;;)
			{
				// Might have been terminated on the main thread
				if(_nConsoleProcessExitCode2.HasValue)
					return null;
				if(_process.HasExited)
					return null;

				try
				{
					// Ask ConEmu for PID
					GetInfoRoot rootinfo = await GetInfoRoot.QueryAsync(this);

					// Check if the process has extied, then we're done
					if(rootinfo.ExitCode.HasValue)
						return rootinfo.ExitCode.Value;

					// If it has started already, must get a PID
					// Await till the process exits and loop to reask conemu for its result
					// If conemu exits too in this time, then it will republish payload exit code as its own exit code, and implementation will use it
					if(rootinfo.Pid.HasValue)
					{
						await WinApi.Helpers.WaitForProcessExitAsync(rootinfo.Pid.Value);
						continue; // Do not wait before retrying
					}
				}
				catch(Exception)
				{
					// Smth failed, wait and retry
				}

				// Await before retrying once more
				await TaskHelpers.Delay(TimeSpan.FromMilliseconds(10));
			}
		}

		[NotNull]
		private Process Init_StartConEmu([NotNull] ConEmuStartInfo startinfo, [NotNull] CommandLineBuilder cmdl)
		{
			if(startinfo == null)
				throw new ArgumentNullException(nameof(startinfo));
			if(cmdl == null)
				throw new ArgumentNullException(nameof(cmdl));

			try
			{
				if(string.IsNullOrEmpty(startinfo.ConEmuExecutablePath))
					throw new InvalidOperationException("Could not run the console emulator. The path to ConEmu.exe could not be detected.");
				if(!File.Exists(startinfo.ConEmuExecutablePath))
					throw new InvalidOperationException($"Missing ConEmu executable at location “{startinfo.ConEmuExecutablePath}”.");
				var processNew = new Process() {StartInfo = new ProcessStartInfo(startinfo.ConEmuExecutablePath, cmdl.ToString()) {UseShellExecute = false}};

				// Bind process termination
				processNew.EnableRaisingEvents = true;
				processNew.Exited += delegate
				{
					// Ensure STA
					Task.Factory.StartNew(scheduler : _schedulerSta, cancellationToken : CancellationToken.None, creationOptions : 0, action : () =>
					{
						// Tear down all objects
						TerminateLifetime();

						// If we haven't separately caught an exit of the payload process
						TryFirePayloadExited(_process.ExitCode /* We haven't caught the exit of the payload process, so we haven't gotten a message with its errorlevel as well. Assume ConEmu propagates its exit code, as there ain't other way for getting it now */);

						// Fire client total exited event
						ConsoleEmulatorExited?.Invoke(this, EventArgs.Empty);
					});
				};

				if(!processNew.Start())
					throw new Win32Exception("The process did not start.");
				return processNew;
			}
			catch(Win32Exception ex)
			{
				TerminateLifetime();
				throw new InvalidOperationException("Could not run the console emulator. " + ex.Message + $" ({ex.NativeErrorCode:X8})" + Environment.NewLine + Environment.NewLine + "Command:" + Environment.NewLine + startinfo.ConEmuExecutablePath + Environment.NewLine + Environment.NewLine + "Arguments:" + Environment.NewLine + cmdl, ex);
			}
		}

		[NotNull]
		private DirectoryInfo Init_TempWorkingFolder()
		{
			var _dirTempWorkingDir = new DirectoryInfo(Path.Combine(Path.Combine(Path.GetTempPath(), "ConEmu"), $"{DateTime.UtcNow.ToString("s").Replace(':', '-')}.{Process.GetCurrentProcess().Id:X8}.{unchecked((uint)RuntimeHelpers.GetHashCode(this)):X8}")); // Prefixed with date-sortable; then PID; then sync table id of this object

			_lifetime.Add(() =>
			{
				try
				{
					if(_dirTempWorkingDir.Exists)
						_dirTempWorkingDir.Delete(true);
				}
				catch(Exception)
				{
					// Not interested
				}
			});

			return _dirTempWorkingDir;
		}

		private void Init_WireEvents([NotNull] ConEmuStartInfo startinfo)
		{
			if(startinfo == null)
				throw new ArgumentNullException(nameof(startinfo));

			// Advise events before they got chance to fire, use event sinks from startinfo for guaranteed delivery
			if(startinfo.PayloadExitedEventSink != null)
				PayloadExited += startinfo.PayloadExitedEventSink;
			if(startinfo.ConsoleEmulatorExitedEventSink != null)
				ConsoleEmulatorExited += startinfo.ConsoleEmulatorExitedEventSink;

			// Re-issue events as async tasks
			// As we advise events before they even fire, the task is guaranteed to get its state
			PayloadExited += (sender, args) => _taskConsoleProcessExit.SetResult(args);
			ConsoleEmulatorExited += delegate { _taskConsoleEmulatorClosed.SetResult(Missing.Value); };
		}

		/// <summary>
		///     <para>Fires when the payload command exits within the terminal. If not <see cref="WhenPayloadProcessExits.CloseTerminal" />, the terminal stays, otherwise it closes also.</para>
		///     <para>For short-lived processes, this event might fire before you sink it. To get notified reliably, use <see cref="WaitForConsolePayloadExitAsync" /> or <see cref="ConEmuStartInfo.PayloadExitedEventSink" />.</para>
		///     <para>If you're reading the ANSI log with <see cref="AnsiStreamChunkReceived" />, it's guaranteed that all the events for the log will be fired before <see cref="PayloadExited" />, and there will be no events afterwards.</para>
		/// </summary>
		public event EventHandler<ProcessExitedEventArgs> PayloadExited;

		private void TerminateLifetime()
		{
			List<Action> items = _lifetime;
			_lifetime.Clear();
			items.Reverse();
			foreach(Action item in items)
				item();
		}

		/// <summary>
		/// Fires the payload exited event if it has not been fired yet.
		/// </summary>
		/// <param name="nPayloadExitCode"></param>
		private void TryFirePayloadExited(int nPayloadExitCode)
		{
			if(_nConsoleProcessExitCode2.HasValue) // It's OK to call it from multiple places, e.g. when payload exit were detected and when ConEmu process itself exits
				return;

			// Make sure the whole ANSI log contents is pumped out before we notify user
			// Dispose call pumps all out and makes sure we never ever fire anything on it after we notify user of PayloadExited; multiple calls to Dispose are OK
			_ansilog?.Dispose();

			// Store exit code
			_nConsoleProcessExitCode2 = nPayloadExitCode;

			// Notify user
			PayloadExited?.Invoke(this, new ProcessExitedEventArgs(nPayloadExitCode));
		}

		public unsafe class HostContext
		{
			public HostContext([NotNull] void* hWndParent, bool isStatusbarVisibleInitial)
			{
				if(hWndParent == null)
					throw new ArgumentNullException(nameof(hWndParent));
				HWndParent = hWndParent;
				IsStatusbarVisibleInitial = isStatusbarVisibleInitial;
			}

			[NotNull]
			public readonly void* HWndParent;

			public readonly bool IsStatusbarVisibleInitial;
		}
	}
}
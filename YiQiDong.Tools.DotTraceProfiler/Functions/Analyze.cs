using System.Diagnostics;
using Quick.Fields;
using Quick.Protocol;
using Quick.Shell.Utils;
using Quick.Utils;
using YiQiDong.Agent;
using YiQiDong.Core;
using YiQiDong.Protocol.V1.Model;

namespace YiQiDong.Tools.DotTraceProfiler.Functions;

public class Analyze : AbstractSessionFunction
{
    public override string Name => "分析";
    public Analyze() : base(null, null) { }
    public Analyze(string sessionId, QpChannel channel) : base(sessionId, channel) { }
    public override AbstractSessionFunction Create(string sessionId, QpChannel channel) => new Analyze(sessionId, channel);

    private string INPUT_PROCESS_ID = nameof(INPUT_PROCESS_ID);
    private string BTN_PROFILING_TYPE = nameof(BTN_PROFILING_TYPE);    
    private string BTN_ATTACH_PROCESS = nameof(BTN_ATTACH_PROCESS);
    private string BTN_SHOWHELP = nameof(BTN_SHOWHELP);
    private string BTN_START = nameof(BTN_START);
    private string BTN_GET_SNAPSHOT = nameof(BTN_GET_SNAPSHOT);
    private string BTN_DROP = nameof(BTN_DROP);
    private string BTN_DETACH_PROCESS = nameof(BTN_DETACH_PROCESS);

    private int targetProcessId = 0;
    private string profilingType = "Sampling";
    private Process targetProcess;    
    private Process analyzeProcess;
    private CancellationTokenSource cts;
    private bool isConnected = false;
    private bool isStarted = false;

    public override FieldForGet[] Execute(FunctionRequest request)
    {
        if (request != null)
        {
            targetProcessId = int.Parse(request.GetFieldValue(nameof(INPUT_PROCESS_ID)));
            profilingType = request.GetFieldValue(nameof(BTN_PROFILING_TYPE));

            if (request.IsFieldIdsMatch("*", BTN_ATTACH_PROCESS))
            {
                try
                {
                    attachToProcess();
                }
                catch (Exception ex)
                {
                    AgentContext.LogError("附加到进程时出错，原因：" + ExceptionUtils.GetExceptionMessage(ex));
                }
            }
            else if (request.IsFieldIdsMatch(BTN_SHOWHELP))
            {
                writeCommand("##dotTrace[\"help\"]");
            }
            else if (request.IsFieldIdsMatch(BTN_START))
            {
                writeCommand("##dotTrace[\"start\"]");
                while (!isStarted)
                    Thread.Sleep(1000);
            }
            else if (request.IsFieldIdsMatch(BTN_GET_SNAPSHOT))
            {
                writeCommand("##dotTrace[\"get-snapshot\"]");
            }
            else if (request.IsFieldIdsMatch(BTN_DROP))
            {
                writeCommand("##dotTrace[\"drop\"]");
            }
            else if (request.IsFieldIdsMatch("*", BTN_DETACH_PROCESS))
            {
                detachFromProcess();
            }
        }
        var list = new List<FieldForGet>()
        {
            new ()
            {
                Type = FieldType.ButtonGroup,
                MarginRight = 2,
                Children =
                [
                    new()
                    {
                        Id = nameof(INPUT_PROCESS_ID),
                        Input_PrependText = "进程编号",
                        Type = FieldType.InputNumber,
                        Input_ReadOnly = isConnected,
                        Value = request?.GetFieldValue(nameof(INPUT_PROCESS_ID)) ?? targetProcessId.ToString(),
                    },
                    new()
                    {
                        Id = BTN_PROFILING_TYPE,
                        Input_PrependText = "分析类型",
                        Type = FieldType.InputSelect,
                        Input_ReadOnly = isConnected,
                        MarginLeft = 1,
                        InputSelect_Options = new Dictionary<string,string>()
                        {
                            ["Sampling"]="Sampling",
                            ["Timeline"]="Timeline"
                        },
                        Value = request?.GetFieldValue(nameof(BTN_PROFILING_TYPE)) ?? profilingType,
                        Input_AppendChildren =
                        [                            
                            isConnected?
                            new()
                            {
                                Id = BTN_DETACH_PROCESS,
                                Name = "分离",
                                Type = FieldType.Button,
                                Theme = FieldTheme.Danger
                            }:
                            new()
                            {
                                Id = BTN_ATTACH_PROCESS,
                                Name = "附加",
                                Type = FieldType.Button,
                                Theme = FieldTheme.Primary
                            }
                        ]
                    },
                ]
            }
        };
        if (isConnected)
            list.Add(new()
            {
                Type = FieldType.ButtonGroup,
                PaddingBottom = 3,
                Children =
                [
                    /*
                    btnList.Add(new()
                    {
                        Id = BTN_SHOWHELP,
                        Name = "显示帮助",
                        Type = FieldType.Button
                    });
                    */
                    new()
                    {
                        Id = BTN_START,
                        Name = "开始",
                        Type = FieldType.Button,
                        Input_Disabled = isStarted
                    },
                    new()
                    {
                        Id = BTN_GET_SNAPSHOT,
                        Name = "获取快照",
                        Type = FieldType.Button,
                        Input_Disabled = !isStarted
                    },
                    new()
                    {
                        Id = BTN_DROP,
                        Name = "丢弃",
                        Type = FieldType.Button,
                        Input_Disabled = !isStarted
                    }
                ]
            });
        return list.ToArray();
    }

    private void writeCommand(string cmd)
    {
        try
        {
            analyzeProcess.StandardInput.WriteLine(cmd);
            analyzeProcess.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            AgentContext.LogError($"发送指令[{cmd}]时出错，原因：" + ExceptionUtils.GetExceptionMessage(ex));
        }
    }

    private void attachToProcess()
    {
        targetProcess = Process.GetProcessById(targetProcessId);
        if (targetProcess == null)
            throw new ArgumentException($"未找到编号为[{targetProcessId}]的进程。");
        cts?.Cancel();
        cts = new();
        var cancellationToken = cts.Token;
        var snapshotFolder = Path.Combine(AgentContext.Container.ContainerFolder, "snapshots");
        if (!Directory.Exists(snapshotFolder))
            Directory.CreateDirectory(snapshotFolder);
        var fileExt = "dtp";
        if (profilingType == "Timeline")
            fileExt = "dtt";
        var snapshotFile = Path.Combine(snapshotFolder, $"snapshot_{targetProcess.Id}_{DateTime.Now:yyyyMMddHHmmss}.{fileExt}");
        var psi = ProcessUtils.CreateProcessStartInfo("dottrace",
            "attach",
            targetProcessId.ToString(),
            "--overwrite",
            "--service-output=on",
            "--service-input=stdin",
            $"--profiling-type={profilingType}",
            "--collect-data-from-start=off",
            $"--save-to={snapshotFile}");
        psi = ProcessUtils.ProcessProcessStartInfo(psi);
        analyzeProcess = Process.Start(psi);
        //处理输出
        Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested && !analyzeProcess.HasExited)
            {
                var line = await analyzeProcess.StandardOutput.ReadLineAsync(cancellationToken);
                if (line.StartsWith("##dotTrace[\"connected\""))
                {
                    isConnected = true;
                    AgentContext.LogInfo($"分析进程[{analyzeProcess.Id}]已附加到进程[{targetProcess.Id}]");
                }
                else if (line.StartsWith("##dotTrace[\"disconnect\""))
                {
                    isConnected = false;
                }
                else if (line.StartsWith("##dotTrace[\"started\""))
                {
                    isStarted = true;
                }
                else if (line.StartsWith("##dotTrace[\"stopped\""))
                {
                    isStarted = false;
                }
                AgentContext.LogInfo(line);
            }
        });
        //处理错误
        Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested && !analyzeProcess.HasExited)
            {
                var line = await analyzeProcess.StandardError.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line))
                {
                    await Task.Delay(100);
                    continue;
                }
                AgentContext.LogError(line);
            }
        });
        //等待进程退出
        Task.Run(async () =>
        {
            try
            {
                await analyzeProcess.WaitForExitAsync();
                isConnected = false;
                isStarted = false;
                cts.Cancel();
                await Task.Delay(1000);
                OnSessionChanged(Execute(null));
                AgentContext.LogInfo($"分析进程[{analyzeProcess.Id}]已退出，退出码：{analyzeProcess.ExitCode}");
            }
            catch (Exception ex)
            {
                AgentContext.LogError($"等待分析进程[{analyzeProcess.Id}]退出时出错，原因：{ExceptionUtils.GetExceptionMessage(ex)}");
            }
        });
        while (true)
        {
            Thread.Sleep(1000);
            if (isConnected || analyzeProcess.HasExited)
                break;
        }
        //定时刷新
        Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                OnSessionChanged(Execute(null));
            }
        });
    }

    private void detachFromProcess()
    {
        if(analyzeProcess.HasExited)
            return;
        writeCommand("##dotTrace[\"disconnect\"]");
        analyzeProcess.WaitForExit(10000);
        if (!analyzeProcess.HasExited)
            analyzeProcess.Kill();
        AgentContext.LogInfo("已从进程分离");
    }

    public override void Start()
    {
        OnSessionChanged(Execute(null));
    }

    public override void Stop()
    {
        detachFromProcess();
        cts?.Cancel();
    }
}

/*
attach命令说明
dotTrace command-line profiler 2025.3.0.3 build 777.0.20251124.223441. Copyright (C) 2025 JetBrains s.r.o.

attach command
~~~~~~~~~~~~~~

Attach profiler to a running .NET process

Usage: dottrace attach [process-spec] [--profiling-type=<profiling-type>] [--save-to=<value>] [--overwrite] [--timeout=<hh:mm:ss>|<value>{s|m|h|d}] [--propagate-exit-code] [--no-check-for-updates] [--use-api] [<core-options>] [--service-output=auto|on|off] [--service-input=null|stdin|<file-path>] [--collect-data-from-start=on|off]

  process-spec: <pid-spec> | <name-spec>
  The specification of the profiled process. To view the list of available processes, do not specify [process-spec]

    <pid-spec>: <pid>
    Specify running process by process ID (PID)
    ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

      <pid>                           PID. Must be an integer

    <name-spec>: <process-name> [--with-max-mem|-M]
    Specify running process by name
    ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

      <process-name>                  Process name (extension is optional)
                                      If name is an integer, specify process name with the extension, e.g. "1234.exe"
                                      (win) or "1234." (linux, macos)
                                      If there are more than one process with the same name, use additional options
                                      to remove the ambiguity:
      [--with-max-mem|-M]               profile only the process with max memory consumption
  [--profiling-type=Timeline|Sampling]
                                  Profiling type. If not specified, the 'Sampling' type is used
                                  Depending on --profiling-type, you can specify additional parameters:

    Timeline: [--collect-native-allocations] [--disable-tpl] [--disable-debug-output] [--download-symbols] [--ask-uac-elevation]

      [--collect-native-allocations]  Collect data on native memory allocation. Important! This option slows down
                                      the profiled application
      [--disable-tpl]                 If specified, may improve performance, but snapshots will lack TPL data: there
                                      will be no 'Task' nodes in Call Tree, 'async' call nodes will be shown without
                                      'await' and 'continuations' parts
      [--disable-debug-output]        Collect debug output
      [--download-symbols]            Helps you to see native functions in the call tree. If enabled, downloads
                                      symbol files from the external sources. Note that this may slow down the process
                                      of getting snapshots
      [--ask-uac-elevation]           If a profiling session requires administrative privileges, dotTrace will ask
                                      for UAC elevation. Otherwise, the session will fail. By default, false

    Sampling: [--time-measurement=PerformanceCounter|CpuInstruction|ThreadTime|ThreadCycleTime]

      [--time-measurement=PerformanceCounter|CpuInstruction|ThreadTime|ThreadCycleTime]
                                      Defines how dotTrace calculates time. Default: PerformanceCounter. For details,
                                      refer to dotTrace online help

  [--save-to=<value>]             Path to the directory (or file) where performance snapshots must be saved. If
                                  not specified, snapshots are saved to the current directory. Note that a snapshot
                                  consists of multiple files: *.dtp, *.dtp.0000, *.dtp.0001, and so on.
  [--overwrite]                   Overwrite the snapshot file if it exists
  [--timeout=<hh:mm:ss>|<value>{s|m|h|d}]
                                  Max profiling session time; once the session ends, the profiler automatically
                                  takes a performance snapshot
  [--propagate-exit-code]         Return exit code of the profiled application. Otherwise, the profiler returns
                                  its own exit codes: 0 for success and 1024 for failure. Note that if profiling
                                  fails, the profiler returns 1024 even if --propagate-exit-code was specified
  [--no-check-for-updates]        Disable checking for updates
  [--use-api]                     Control profiling session using the API

  [<core-options>]                Advanced options.
                                  For details, run: dottrace help core-options

  [--service-output=auto|on|off]  Enable output service messages (sent to stdout).
                                  If auto, output messages will be enabled only if --service-input is also enabled.
                                  To see details, use: dottrace help service-messages
  [--service-input=null|stdin|<file-path>]
                                  Enable input service messages.
                                  To control profiling by writing messages to stdin, use --service-input=stdin.
                                  By default, disabled.
                                  Ignored if --use-api is specified.
                                  To see details, use: dottrace help service-messages

  [--collect-data-from-start=on|off]
                                  Start collecting data right after the start of profiling session. By default,
                                  on.
                                  Ignored if either --use-api or --service-input=null is specified

  Examples
  ~~~~~~~~

    Profile an already running application with PID=1234 using the default 'Sampling' mode; save a snapshot to the
    'snapshot.dtp' file in the current directory on process exit. If the process does not finish in 30 seconds,
    take a snapshot and detach:
      dottrace attach 1234 --save-to=snapshot.dtp --timeout=30s
*/

/*
控制台交互说明
You can control profiling using service messages
To control profiling by writing messages to stdin of dottrace, use --service-input=stdin
If the profiled process is a console application, you can also communicate with dottrace using a file on the disk (instead of stdin). To do this, use --service-input=path\messages.svc

stdin messages:
~~~~~~~~~~~~~~~

  ##dotTrace["start", {pid: 1234}]
                                  Start collecting performance data.
                                  If pid is not specified, the command is applied to every profiled process.
  ##dotTrace["get-snapshot", {pid: 1234}]
                                  Get snapshot and stop collecting new data.
                                  If pid is not specified, the command is applied to every profiled process.
                                  Note: after taking a snapshot, the profiler goes to 'stopped' state.
                                  To continue collecting data, run "start" command.
  ##dotTrace["drop", {pid: 1234}] Discard all collected data and stop collecting new data.
                                  If pid is not specified, the command is applied to every profiled process.
                                  Note: after dropping a snapshot, the profiler goes to 'stopped' state.
                                  To continue collecting data, run "start" command.
  ##dotTrace["disconnect", {pid: 1234}]
                                  Disconnect profiler.
                                  If you started profiling with 'start', the profiled process will be killed.
                                  If you started profiling with 'attach', the profiler will detach from the process.
                                  If pid is not specified, the command is applied to every profiled process.
  Important: Messages must always start with a new line and end with a carriage return!

stdout messages:
~~~~~~~~~~~~~~~~

  ##dotTrace["ready"]             Profiler is ready to connect to processes
  ##dotTrace["connected", {pid: 1234, path:"executable"}]
                                  Profiler is connected to the profiled process
  ##dotTrace["started", {pid: 1234, path:"executable"}]
                                  Profiler started collecting performance data
  ##dotTrace["stopped", {pid: 1234, path:"executable"}]
                                  Profiler stopped collecting performance data
  ##dotTrace["snapshot-saved", {pid: 1234, filename:"path-to-snapshot-index-file"}]
                                  Snapshot is taken
  ##dotTrace["disconnected", {pid: 1234, path:"executable"}]
                                  Profiler disconnected from the profiled process
*/
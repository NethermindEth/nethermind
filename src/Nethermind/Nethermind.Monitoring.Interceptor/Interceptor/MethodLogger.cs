using System;
using System.Diagnostics;
using System.Reflection;
using PostSharp.Aspects;
using PostSharp.Serialization;
using System.Linq;

[PSerializable]
[DebuggerStepThrough]
[AttributeUsage(AttributeTargets.Method)]
public class MonitorAttribute : OnGeneralMethodBoundaryAspect
{

    [PNonSerialized] private (Assembly Assembly, string Function) CallSourceType;

    private InterceptionMode _interceptionMode { get; set; }
    private LogDestination _logDestination { get; set; }
    private TimeUnit _timeUnit { get; set; }
    private int _periodBetweenLogsWait { get; set; }
    private string _logFilePath { get; set; }
    private DateTime? previousLogTime;
    private bool ShouldLog(int periodTicks)
    {
        if (previousLogTime is null)
        {
            previousLogTime = DateTime.UtcNow;
            return true;
        }

        var now = DateTime.UtcNow;
        if (DateTime.UtcNow.Ticks - previousLogTime.Value.Ticks > periodTicks)
        {
            previousLogTime = now;
            return true;
        }

        return false;
    }

    private bool IsInitialized = false;
    private string PropertyName => $"{CallSourceType.Function}{_interceptionMode}";
    public MonitorAttribute(
        InterceptionMode InterceptionMode,
        LogDestination LogDestination, String FilePath = null,
        TimeUnit TimeUnit = TimeUnit.Temporal, int WaitInBetweenLogs = 100)
    {
        _interceptionMode = InterceptionMode;
        _logDestination = LogDestination;
        _periodBetweenLogsWait = WaitInBetweenLogs;
        _timeUnit = TimeUnit;
        if (LogDestination == LogDestination.Logger)
        {
            _logFilePath = FilePath;
        }
    }
    public override MetricsMetadata OnStarting(MethodInterceptionArgs args)
    {
        if (!IsInitialized)
        {
            var method = args.Method;
            CallSourceType = (method.Module.Assembly, method.Name);
            if (!MetricsMetadataExtensions.CallCountKeeper.ContainsKey(CallSourceType.Function))
            {
                MetricsMetadataExtensions.CallCountKeeper[CallSourceType.Function] = 0;
                MetricsMetadataExtensions.ExceptionCountKeeper[CallSourceType.Function] = 0;
            }

            IsInitialized = true;
        }

        MetricsMetadata AttachedLog = args;
        AttachedLog.EmbeddedResource = new Stopwatch();
        AttachedLog.MethodQualifiedName = args.Method.Name;
        AttachedLog.StartTime = DateTime.Now;

        MetricsMetadataExtensions.CallCountKeeper[CallSourceType.Function]++;

        (AttachedLog.EmbeddedResource as Stopwatch).Start();
        return AttachedLog;
    }

    public override void OnFailure(MetricsMetadata innerLogs)
    {
        var failures = MetricsMetadataExtensions.ExceptionCountKeeper[CallSourceType.Function]++;
        var message = $"Error :: {DateTime.Now} :>> Logs : \n {innerLogs.ToString(_interceptionMode)}, failure N: {failures}\n";
        SendToLogDestination(innerLogs, message);
    }

    public override void OnCompletion(MetricsMetadata logs)
    {
        var timer = logs.EmbeddedResource as Stopwatch;
        timer.Stop();

        if (!ShouldLog(_periodBetweenLogsWait))
        {
            return;
        }

        logs.FinishTime = DateTime.Now;
        logs.ExecutionTime = timer.Elapsed;
        var message = $"Logs :: {DateTime.Now} :>> Logs : \n {logs.ToString(_interceptionMode, _timeUnit)} \n";
        SendToLogDestination(logs, message);
    }
    private void SendToLogDestination(MetricsMetadata innerLogs, string message)
    {
        switch (_logDestination)
        {
            case LogDestination.Console:
                Console.WriteLine(message);
                break;
            case LogDestination.Debug:
                Debug.WriteLine(message);
                break;
            case LogDestination.Prometheus:
                var prometheusSinkType = CallSourceType.Assembly.GetTypes().Where(t => t.FullName == "Plugin.Metrics").Single();
                prometheusSinkType?
                    .GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Static)
                    .SetValue(null, _interceptionMode switch
                    {
                        InterceptionMode.ExecutionTime => innerLogs.ExecutionTime,
                        InterceptionMode.CallCount => innerLogs.CallCount,
                        InterceptionMode.Failures => innerLogs.Failures,
                        InterceptionMode.MetadataLog => throw new NotImplementedException()
                    });
                break;
            default:
                break;
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Linq;
using Nethermind.Core.Collections;
using NLog;
using NLog.Config;
using NLog.LayoutRenderers;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Seq;
using NLog.Targets.Wrappers;

namespace Nethermind.Runner.Logging;

public static class NLogConfigurator
{
    private static int _customRenderersRegistered;

    /// <summary>
    /// Replaces the layout of the console target with a structured <see cref="JsonLayout"/>
    /// matching the requested format. Has no effect when the format is <c>plain</c>.
    /// </summary>
    /// <param name="format">One of <c>plain</c>, <c>ecs</c>, <c>gcp</c>, <c>logstash</c>, <c>gelf</c> (case-insensitive).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="format"/> is not a recognized value.</exception>
    public static void ConfigureConsoleFormat(string format)
    {
        ArgumentNullException.ThrowIfNull(format);

        LoggingFormat parsed = ParseFormat(format);

        if (parsed == LoggingFormat.Plain) return;

        LoggingConfiguration? loggingConfiguration = LogManager.Configuration;

        if (loggingConfiguration is null) return;

        EnsureCustomRenderers();

        Layout layout = BuildLayout(parsed);

        foreach (Target target in loggingConfiguration.AllTargets)
        {
            Target unwrapped = target is WrapperTargetBase wrapper ? wrapper.WrappedTarget : target;

            if (unwrapped is ConsoleTarget consoleTarget)
            {
                consoleTarget.Layout = layout;
            }
            else if (unwrapped is ColoredConsoleTarget coloredTarget)
            {
                coloredTarget.Layout = layout;
                coloredTarget.RowHighlightingRules.Clear();
                coloredTarget.WordHighlightingRules.Clear();
            }
        }

        LogManager.ReconfigExistingLoggers();
    }

    private static LoggingFormat ParseFormat(string format) => format.Trim().ToUpperInvariant() switch
    {
        "PLAIN" => LoggingFormat.Plain,
        "ECS" => LoggingFormat.Ecs,
        "GCP" => LoggingFormat.Gcp,
        "LOGSTASH" => LoggingFormat.Logstash,
        "GELF" => LoggingFormat.Gelf,
        _ => throw new ArgumentException(
            $"Unknown logging format '{format}'. Allowed values: plain, ecs, gcp, logstash, gelf.",
            nameof(format))
    };

    private static Layout BuildLayout(LoggingFormat format) => format switch
    {
        LoggingFormat.Ecs => BuildEcsLayout(),
        LoggingFormat.Gcp => BuildGcpLayout(),
        LoggingFormat.Logstash => BuildLogstashLayout(),
        LoggingFormat.Gelf => BuildGelfLayout(),
        _ => throw new InvalidOperationException()
    };

    private static JsonLayout BuildEcsLayout() => new()
    {
        SuppressSpaces = true,
        Attributes =
        {
            new JsonAttribute("@timestamp", Layout.FromString("${date:universalTime=true:format=yyyy-MM-ddTHH\\:mm\\:ss.fffZ}")),
            new JsonAttribute("log.level", Layout.FromString("${level:lowercase=true}")),
            new JsonAttribute("message", Layout.FromString("${message}")),
            new JsonAttribute("ecs.version", Layout.FromString("8.11.0")),
            new JsonAttribute("log.logger", Layout.FromString("${logger}")),
            new JsonAttribute("process.thread.id", Layout.FromString("${threadid}")) { Encode = false },
            new JsonAttribute("error.type", Layout.FromString("${exception:format=type}")),
            new JsonAttribute("error.message", Layout.FromString("${exception:format=message}")),
            new JsonAttribute("error.stack_trace", Layout.FromString("${exception:format=tostring}"))
        }
    };

    private static JsonLayout BuildGcpLayout() => new()
    {
        SuppressSpaces = true,
        Attributes =
        {
            new JsonAttribute("severity", Layout.FromString("${gcp-severity}")),
            new JsonAttribute("time", Layout.FromString("${date:universalTime=true:format=yyyy-MM-ddTHH\\:mm\\:ss.fffZ}")),
            new JsonAttribute("message", Layout.FromString("${message}${onexception:inner=\\: ${exception:format=tostring}}")),
            new JsonAttribute("logger", Layout.FromString("${logger}")),
            new JsonAttribute("thread", Layout.FromString("${threadid}")) { Encode = false }
        }
    };

    private static JsonLayout BuildLogstashLayout() => new()
    {
        SuppressSpaces = true,
        Attributes =
        {
            new JsonAttribute("@timestamp", Layout.FromString("${date:universalTime=true:format=yyyy-MM-ddTHH\\:mm\\:ss.fffZ}")),
            new JsonAttribute("@version", Layout.FromString("1")),
            new JsonAttribute("message", Layout.FromString("${message}")),
            new JsonAttribute("level", Layout.FromString("${level:upperCase=true}")),
            new JsonAttribute("logger_name", Layout.FromString("${logger}")),
            new JsonAttribute("thread_name", Layout.FromString("${threadid}")),
            new JsonAttribute("host", Layout.FromString("${machinename}")),
            new JsonAttribute("stack_trace", Layout.FromString("${exception:format=tostring}"))
        }
    };

    private static JsonLayout BuildGelfLayout() => new()
    {
        SuppressSpaces = true,
        Attributes =
        {
            new JsonAttribute("version", Layout.FromString("1.1")),
            new JsonAttribute("host", Layout.FromString("${machinename}")),
            new JsonAttribute("short_message", Layout.FromString("${message}")),
            new JsonAttribute("full_message", Layout.FromString("${onexception:inner=${message}\\n${exception:format=tostring}}")),
            new JsonAttribute("timestamp", Layout.FromString("${date:universalTime=true:format=yyyy-MM-ddTHH\\:mm\\:ss.fffZ}")),
            new JsonAttribute("level", Layout.FromString("${gelf-level}")) { Encode = false },
            new JsonAttribute("_logger", Layout.FromString("${logger}")),
            new JsonAttribute("_thread", Layout.FromString("${threadid}"))
        }
    };

    private enum LoggingFormat
    {
        Plain,
        Ecs,
        Gcp,
        Logstash,
        Gelf
    }

    private static void EnsureCustomRenderers()
    {
        if (System.Threading.Interlocked.Exchange(ref _customRenderersRegistered, 1) == 1) return;

        LogManager.Setup().SetupExtensions(ext =>
        {
            ext.RegisterLayoutRenderer("gcp-severity", logEvent => MapGcpSeverity(logEvent.Level));
            ext.RegisterLayoutRenderer("gelf-level", logEvent => MapGelfLevel(logEvent.Level).ToString(CultureInfo.InvariantCulture));
        });
    }

    private static string MapGcpSeverity(LogLevel level)
    {
        // https://cloud.google.com/logging/docs/reference/v2/rest/v2/LogEntry#LogSeverity
        if (level == LogLevel.Trace) return "DEBUG";
        if (level == LogLevel.Debug) return "DEBUG";
        if (level == LogLevel.Info) return "INFO";
        if (level == LogLevel.Warn) return "WARNING";
        if (level == LogLevel.Error) return "ERROR";
        if (level == LogLevel.Fatal) return "CRITICAL";
        return "DEFAULT";
    }

    private static int MapGelfLevel(LogLevel level)
    {
        // RFC 5424 syslog severity used by GELF 1.1
        if (level == LogLevel.Trace) return 7;
        if (level == LogLevel.Debug) return 7;
        if (level == LogLevel.Info) return 6;
        if (level == LogLevel.Warn) return 4;
        if (level == LogLevel.Error) return 3;
        if (level == LogLevel.Fatal) return 2;
        return 6;
    }

    public static void ConfigureSeqBufferTarget(
        string url = "http://localhost:5341",
        string apiKey = "",
        string minLevel = "Off")
    {
        LoggingConfiguration loggingConfiguration = LogManager.Configuration;
        if (loggingConfiguration is not null)
        {
            if (loggingConfiguration.AllTargets is not null)
            {
                foreach (SeqTarget target in loggingConfiguration.AllTargets.OfType<SeqTarget>())
                {
                    target.ApiKey = apiKey;
                    target.ServerUrl = url;
                    foreach (LoggingRule? rule in loggingConfiguration.LoggingRules)
                    {
                        foreach (Target? ruleTarget in rule.Targets)
                        {
                            if (ruleTarget.Name == "seq" && rule.LoggerNamePattern == "*")
                            {
                                rule.EnableLoggingForLevels(LogLevel.FromString(minLevel), LogLevel.Fatal);
                            }
                        }
                    }
                }
            }

            // // // re-initialize single target
            loggingConfiguration.AllTargets?.OfType<SeqTarget>().ForEach(static t => t.Dispose());
            LogManager.ReconfigExistingLoggers();
        }
    }

    public static void ClearSeqTarget()
    {
        LoggingConfiguration loggingConfiguration = LogManager.Configuration;
        loggingConfiguration?.RemoveTarget("seq");
    }

    public static void ConfigureLogLevels(string logLevel)
    {
        LogLevel nLogLevel = logLevel.ToUpperInvariant() switch
        {
            "OFF" => LogLevel.Off,
            "ERROR" => LogLevel.Error,
            "WARN" => LogLevel.Warn,
            "INFO" => LogLevel.Info,
            "DEBUG" => LogLevel.Debug,
            "TRACE" => LogLevel.Trace,
            _ => LogLevel.Info
        };

        //Console.WriteLine($"Enabling log level override: {logLevel.ToUpperInvariant()}");

        // There are some rules for which we don't want to override the log level
        // but instead preserve the original config defined in the 'NLog.config' file
        string[] ignoredRuleNames =
        {
            "JsonWebAPI*",
            "JsonWebAPI.Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService",
        };
        foreach (LoggingRule rule in LogManager.Configuration.LoggingRules)
        {
            if (ignoredRuleNames.Contains(rule.LoggerNamePattern)) { continue; }

            foreach (Target ruleTarget in rule.Targets)
            {
                if (ruleTarget.Name != "seq")
                {
                    //Console.WriteLine($"{ruleTarget.Name} TEST");
                    rule.DisableLoggingForLevels(LogLevel.Trace, nLogLevel);
                    rule.EnableLoggingForLevels(nLogLevel, LogLevel.Off);
                }
            }
        }

        LogManager.ReconfigExistingLoggers();
    }
}

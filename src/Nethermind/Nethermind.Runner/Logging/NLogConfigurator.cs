// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Nethermind.Core.Collections;
using NLog;
using NLog.Common;
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

    // Regex pattern matching ANSI CSI SGR escape sequences (e.g. "\x1B[31m"). Wrapped around ${message}
    // for JSON layouts so coloured upstream log strings do not pollute the rendered JSON payload.
    private const string AnsiStripPattern = @"\x1B\[[0-9;]*m";

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

        if (loggingConfiguration is null)
        {
            InternalLogger.Debug("NLogConfigurator: skipping console format swap, LogManager.Configuration is null.");
            return;
        }

        EnsureCustomRenderers();

        Layout layout = BuildLayout(parsed);

        // AllTargets already enumerates wrapped inner targets, so OfType is enough.
        foreach (ConsoleTarget consoleTarget in loggingConfiguration.AllTargets.OfType<ConsoleTarget>())
        {
            consoleTarget.Layout = layout;
        }

        foreach (ColoredConsoleTarget coloredTarget in loggingConfiguration.AllTargets.OfType<ColoredConsoleTarget>())
        {
            coloredTarget.Layout = layout;
            coloredTarget.RowHighlightingRules.Clear();
            coloredTarget.WordHighlightingRules.Clear();
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

    // ECS uses 100-ns subsecond precision (date_nanos compatible). Costs nothing and avoids collisions
    // on busy nodes that emit hundreds of events per block.
    private static JsonLayout BuildEcsLayout() => new()
    {
        SuppressSpaces = true,
        Attributes =
        {
            new JsonAttribute("@timestamp", Layout.FromString("${date:universalTime=true:format=yyyy-MM-ddTHH\\:mm\\:ss.fffffffZ}")),
            new JsonAttribute("log.level", Layout.FromString("${level:lowercase=true}")),
            new JsonAttribute("message", MessageLayout()),
            new JsonAttribute("ecs.version", Layout.FromString("8.11.0")),
            new JsonAttribute("log.logger", Layout.FromString("${logger}")),
            new JsonAttribute("process.thread.id", Layout.FromString("${threadid}")) { Encode = false },
            new JsonAttribute("error.type", Layout.FromString("${exception:format=type}")) { IncludeEmptyValue = false },
            new JsonAttribute("error.message", Layout.FromString("${exception:format=message}")) { IncludeEmptyValue = false },
            new JsonAttribute("error.stack_trace", Layout.FromString("${exception:format=tostring}")) { IncludeEmptyValue = false }
        }
    };

    private static JsonLayout BuildGcpLayout() => new()
    {
        SuppressSpaces = true,
        Attributes =
        {
            new JsonAttribute("severity", Layout.FromString("${gcp-severity}")),
            new JsonAttribute("time", Layout.FromString("${date:universalTime=true:format=yyyy-MM-ddTHH\\:mm\\:ss.fffffffZ}")),
            new JsonAttribute("message", Layout.FromString($"${{replace:searchFor={AnsiStripPattern}:replaceWith=:regex=true:inner=${{message}}}}${{onexception:inner=\\: ${{exception:format=tostring}}}}")),
            new JsonAttribute("logger", Layout.FromString("${logger}")),
            new JsonAttribute("thread", Layout.FromString("${threadid}")) { Encode = false }
        }
    };

    private static JsonLayout BuildLogstashLayout() => new()
    {
        SuppressSpaces = true,
        Attributes =
        {
            new JsonAttribute("@timestamp", Layout.FromString("${date:universalTime=true:format=yyyy-MM-ddTHH\\:mm\\:ss.fffffffZ}")),
            new JsonAttribute("@version", Layout.FromString("1")),
            new JsonAttribute("message", MessageLayout()),
            new JsonAttribute("level", Layout.FromString("${level:upperCase=true}")),
            new JsonAttribute("logger_name", Layout.FromString("${logger}")),
            // thread_name is a string per Logstash convention; leave default Encode=true.
            new JsonAttribute("thread_name", Layout.FromString("${threadid}")),
            new JsonAttribute("host", Layout.FromString("${machinename}")),
            new JsonAttribute("stack_trace", Layout.FromString("${exception:format=tostring}")) { IncludeEmptyValue = false }
        }
    };

    // GELF 1.1: `timestamp` is "seconds since UNIX epoch with optional decimal places for milliseconds"
    // and must be numeric. Encode=false leaves the value unquoted in the rendered JSON.
    // See https://go2docs.graylog.org/current/getting_in_log_data/gelf.html
    private static JsonLayout BuildGelfLayout() => new()
    {
        SuppressSpaces = true,
        Attributes =
        {
            new JsonAttribute("version", Layout.FromString("1.1")),
            new JsonAttribute("host", Layout.FromString("${machinename}")),
            new JsonAttribute("short_message", MessageLayout()),
            new JsonAttribute("full_message", Layout.FromString($"${{onexception:inner=${{replace:searchFor={AnsiStripPattern}:replaceWith=:regex=true:inner=${{message}}}}\\n${{exception:format=tostring}}}}")) { IncludeEmptyValue = false },
            new JsonAttribute("timestamp", Layout.FromString("${gelf-timestamp}")) { Encode = false },
            new JsonAttribute("level", Layout.FromString("${gelf-level}")) { Encode = false },
            // _logger / _thread are GELF user-defined string fields — keep default Encode=true so they quote.
            new JsonAttribute("_logger", Layout.FromString("${logger}")),
            new JsonAttribute("_thread", Layout.FromString("${threadid}"))
        }
    };

    private static Layout MessageLayout() =>
        Layout.FromString($"${{replace:searchFor={AnsiStripPattern}:replaceWith=:regex=true:inner=${{message}}}}");

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
        if (Volatile.Read(ref _customRenderersRegistered) == 1) return;
        if (Interlocked.CompareExchange(ref _customRenderersRegistered, 1, 0) != 0) return;

        LogManager.Setup().SetupExtensions(ext =>
        {
            ext.RegisterLayoutRenderer("gcp-severity", logEvent => MapGcpSeverity(logEvent.Level));
            ext.RegisterLayoutRenderer("gelf-level", logEvent => MapGelfLevel(logEvent.Level).ToString(CultureInfo.InvariantCulture));
            ext.RegisterLayoutRenderer("gelf-timestamp", logEvent =>
            {
                double seconds = (logEvent.TimeStamp.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
                return seconds.ToString("0.000", CultureInfo.InvariantCulture);
            });
        });
    }

    // https://cloud.google.com/logging/docs/reference/v2/rest/v2/LogEntry#LogSeverity
    private static string MapGcpSeverity(LogLevel level) => level.Ordinal switch
    {
        0 => "DEBUG",    // Trace
        1 => "DEBUG",    // Debug
        2 => "INFO",
        3 => "WARNING",
        4 => "ERROR",
        5 => "CRITICAL", // Fatal
        _ => "DEFAULT"
    };

    // RFC 5424 syslog severity used by GELF 1.1
    private static int MapGelfLevel(LogLevel level) => level.Ordinal switch
    {
        0 => 7, // Trace -> debug
        1 => 7, // Debug
        2 => 6, // Info -> informational
        3 => 4, // Warn -> warning
        4 => 3, // Error
        5 => 2, // Fatal -> critical
        _ => 6
    };

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

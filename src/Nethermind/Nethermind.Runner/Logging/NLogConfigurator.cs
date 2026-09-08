// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml;
using Nethermind.Core.Collections;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Seq;

namespace Nethermind.Runner.Logging;

public static class NLogConfigurator
{
    private static int _customRenderersRegistered;

    private const string EmbeddedConsoleTargetName = "json-console";

    // Each format ships as an embedded NLog XML resource under Logging/Layouts/. The C# side stays
    // a thin loader: parse the resource, copy the resulting Layout onto the existing console
    // target(s). The XML is the source of truth for field shape and lets operators inspect the
    // emitted JSON layout without reading code. Custom renderers (gcp-severity, gelf-level,
    // gelf-timestamp) stay in C# because they need access to LogEventInfo.
    private static readonly IReadOnlyDictionary<LoggingFormat, string> EmbeddedLayoutResources =
        new Dictionary<LoggingFormat, string>
        {
            [LoggingFormat.Ecs] = "Nethermind.Runner.Logging.Layouts.ecs.layout.xml",
            [LoggingFormat.Gcp] = "Nethermind.Runner.Logging.Layouts.gcp.layout.xml",
            [LoggingFormat.Logstash] = "Nethermind.Runner.Logging.Layouts.logstash.layout.xml",
            [LoggingFormat.Gelf] = "Nethermind.Runner.Logging.Layouts.gelf.layout.xml"
        };

    /// <summary>
    /// Replaces the layout of the console target with a structured <see cref="JsonLayout"/>
    /// loaded from the embedded XML resource for the requested format. Has no effect when
    /// the format is <c>plain</c>.
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

        Layout layout = LoadEmbeddedLayout(parsed);

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

    private static Layout LoadEmbeddedLayout(LoggingFormat format)
    {
        if (!EmbeddedLayoutResources.TryGetValue(format, out string? resourceName))
        {
            throw new InvalidOperationException($"No embedded layout registered for {format}.");
        }

        Assembly assembly = typeof(NLogConfigurator).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded layout resource '{resourceName}' not found.");

        // XmlLoggingConfiguration parses the snippet into a temporary configuration whose only
        // purpose is to give us a fully constructed Layout. We never register the temp config with
        // LogManager — we just lift the Layout off the configured target.
        using XmlReader reader = XmlReader.Create(stream);
        XmlLoggingConfiguration embeddedConfig = new(reader, fileName: resourceName);

        // ConfiguredNamedTargets only enumerates targets that have been registered with the
        // configuration (no <rules> reference required, unlike FindTargetByName).
        Target target = embeddedConfig.ConfiguredNamedTargets
            .FirstOrDefault(t => string.Equals(t.Name, EmbeddedConsoleTargetName, StringComparison.Ordinal))
            ?? embeddedConfig.AllTargets.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Embedded layout '{resourceName}' does not contain any target (expected '{EmbeddedConsoleTargetName}').");

        return (target as TargetWithLayout)?.Layout
            ?? throw new InvalidOperationException(
                $"Embedded layout '{resourceName}' did not produce a Layout on target '{target.Name}'.");
    }

    internal enum LoggingFormat
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

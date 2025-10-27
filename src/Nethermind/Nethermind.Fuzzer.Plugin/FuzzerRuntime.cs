// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Logging;
using NLog;
using NLog.Config;
using NLog.Targets;
using Level = NLog.LogLevel;
using NethermindLogger = Nethermind.Logging.ILogger;

namespace Nethermind.Fuzzer.Plugin;

internal sealed class FuzzerRuntime
{
    private readonly IFuzzerConfig _config;
    private readonly IInitConfig _initConfig;
    private readonly NethermindLogger _logger;
    private readonly IProcessExitSource _processExit;
    private readonly IReadOnlyList<ThresholdState> _thresholds;
    private readonly HashSet<string> _normalizedSeen = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly string _triggerFilePath;

    private bool _ready;
    private bool _triggered;

    private const int DefaultThresholdCount = 20;

    private static readonly Regex TimestampRegex = new(@"^\s*\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HexRegex = new(@"0x[0-9a-fA-F]{6,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LongNumberRegex = new(@"\b\d{6,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRegex = new(@"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FuzzerRuntime(IFuzzerConfig config, IInitConfig initConfig, NethermindLogger logger, IProcessExitSource processExit)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig));
        _logger = logger;
        _processExit = processExit ?? throw new ArgumentNullException(nameof(processExit));
        _thresholds = ParseThresholds(config.ThresholdPhrases);
        _triggerFilePath = ResolveTriggerFilePath(initConfig, config.TriggerFilePath);
    }

    public bool AttachToLoggingPipeline()
    {
        try
        {
            LoggingConfiguration configuration = LogManager.Configuration ?? new LoggingConfiguration();
            if (configuration.AllTargets.OfType<FuzzerCaptureTarget>().Any())
            {
                return true;
            }

            FuzzerCaptureTarget target = new(this)
            {
                Name = FuzzerCaptureTarget.TargetName
            };

            configuration.AddTarget(target.Name, target);
            LoggingRule rule = new("*", Level.Trace, target);
            configuration.LoggingRules.Add(rule);
            LogManager.Configuration = configuration;
            LogManager.ReconfigExistingLoggers();

            if (_logger.IsInfo)
            {
                _logger.Info($"Fuzzer plugin attached to logging pipeline. Trigger file path: {_triggerFilePath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error("Fuzzer plugin failed to attach to logging pipeline.", ex);
            }

            return false;
        }
    }

    internal void Handle(LogEventInfo logEvent)
    {
        if (logEvent is null || _triggered)
        {
            return;
        }

        if (IsOwnLog(logEvent.LoggerName))
        {
            return;
        }

        string message = ExtractMessage(logEvent);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!EnsureReadiness(message))
        {
            return;
        }

        if (ShouldIgnoreThresholdPhrase(message))
        {
            return;
        }

        string normalized = Normalize(message);
        if (normalized.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_triggered)
            {
                return;
            }

            if (!_normalizedSeen.Add(normalized))
            {
                return;
            }

            _triggered = true;
        }

        Trigger(logEvent, message, normalized);
    }

    private bool EnsureReadiness(string message)
    {
        lock (_sync)
        {
            if (_ready)
            {
                return true;
            }

            bool progressed = false;
            foreach (ThresholdState threshold in _thresholds)
            {
                if (threshold.IsSatisfied)
                {
                    continue;
                }

                if (ContainsPhrase(message, threshold.Phrase))
                {
                    threshold.Count++;
                    progressed = true;
                }
            }

            if (_thresholds.Count == 0 || _thresholds.All(static t => t.IsSatisfied))
            {
                _ready = true;
                if (_logger.IsInfo)
                {
                    _logger.Info("Fuzzer readiness reached. Monitoring for new log entries.");
                }

                return true;
            }

            if (progressed && _logger.IsTrace)
            {
                _logger.Trace($"Fuzzer readiness progress: {DescribeThresholds()}");
            }

            return false;
        }
    }

    private bool ShouldIgnoreThresholdPhrase(string message)
        => _thresholds.Any(threshold => ContainsPhrase(message, threshold.Phrase));

    private string DescribeThresholds()
        => string.Join(", ",
            _thresholds.Select(t => $"{t.Phrase}: {Math.Min(t.Count, t.Required)}/{t.Required}"));

    private void Trigger(LogEventInfo logEvent, string message, string normalized)
    {
        if (_logger.IsWarn)
        {
            _logger.Warn($"Fuzzer detected new log entry. Writing trigger details to {_triggerFilePath} and terminating.");
        }

        try
        {
            PersistTrigger(logEvent, message, normalized);
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Failed to persist fuzzer trigger details to {_triggerFilePath}.", ex);
            }
        }
        finally
        {
            TerminateProcess();
        }
    }

    private void PersistTrigger(LogEventInfo logEvent, string message, string normalized)
    {
        string? directory = Path.GetDirectoryName(_triggerFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        StringBuilder builder = new();
        builder.AppendLine($"Timestamp: {DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Logger: {logEvent.LoggerName}");
        builder.AppendLine($"Level: {logEvent.Level}");
        builder.AppendLine("Message:");
        builder.AppendLine(message);
        builder.AppendLine("Normalized:");
        builder.AppendLine(normalized);
        if (logEvent.Exception is not null)
        {
            builder.AppendLine("Exception:");
            builder.AppendLine(logEvent.Exception.ToString());
        }

        File.WriteAllText(_triggerFilePath, builder.ToString());
    }

    private void TerminateProcess()
    {
        try
        {
            _processExit.Exit(ExitCodes.GeneralError);
        }
        catch
        {
            // ignored – best effort before forcing termination
        }

        try
        {
            Process.GetCurrentProcess().Kill(true);
        }
        catch (Exception killEx)
        {
            Environment.FailFast("Fuzzer plugin triggered termination.", killEx);
        }
    }

    private static bool IsOwnLog(string? loggerName)
        => !string.IsNullOrEmpty(loggerName) &&
           loggerName.StartsWith("Nethermind.Fuzzer.Plugin", StringComparison.Ordinal);

    private static string ExtractMessage(LogEventInfo logEvent)
    {
        string message = logEvent.FormattedMessage ?? logEvent.Message ?? string.Empty;
        if (logEvent.Exception is not null)
        {
            message = $"{message} | Exception: {logEvent.Exception}";
        }

        return message;
    }

    private static bool ContainsPhrase(string message, string phrase)
        => message.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Normalize(string message)
    {
        string result = message.Trim();
        if (result.Length == 0)
        {
            return string.Empty;
        }

        result = TimestampRegex.Replace(result, string.Empty);
        result = HexRegex.Replace(result, "0xHASH");
        result = LongNumberRegex.Replace(result, "#NUM#");
        result = WhitespaceRegex.Replace(result, " ").Trim();
        return result;
    }

    private static string ResolveTriggerFilePath(IInitConfig initConfig, string? configuredPath)
    {
        string logDirectory = initConfig.LogDirectory.GetApplicationResourcePath(initConfig.DataDir);
        string resolved = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(logDirectory, "fuzzer-trigger.log")
            : configuredPath.GetApplicationResourcePath(logDirectory);

        return Path.GetFullPath(resolved);
    }

    private static IReadOnlyList<ThresholdState> ParseThresholds(string? rawValue)
    {
        List<ThresholdState> thresholds = new();
        string value = rawValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            thresholds.Add(new ThresholdState("Processed", DefaultThresholdCount));
            thresholds.Add(new ThresholdState("Synced Chain Head", DefaultThresholdCount));
            return thresholds;
        }

        string[] entries = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string entry in entries)
        {
            string phrase = entry;
            int required = DefaultThresholdCount;

            int separatorIndex = entry.IndexOf(':');
            if (separatorIndex >= 0)
            {
                phrase = entry[..separatorIndex].Trim();
                string countSegment = entry[(separatorIndex + 1)..].Trim();
                if (int.TryParse(countSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                {
                    required = parsed;
                }
            }

            if (string.IsNullOrWhiteSpace(phrase))
            {
                continue;
            }

            thresholds.Add(new ThresholdState(phrase, required));
        }

        if (thresholds.Count == 0)
        {
            thresholds.Add(new ThresholdState("Processed", DefaultThresholdCount));
            thresholds.Add(new ThresholdState("Synced Chain Head", DefaultThresholdCount));
        }

        return thresholds;
    }

    private sealed class ThresholdState
    {
        public ThresholdState(string phrase, int required)
        {
            Phrase = phrase;
            Required = required;
        }

        public string Phrase { get; }
        public int Required { get; }
        public int Count { get; set; }
        public bool IsSatisfied => Count >= Required;
    }
}

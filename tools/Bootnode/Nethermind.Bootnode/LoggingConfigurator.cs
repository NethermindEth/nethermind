// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NLog;
using NLog.Config;
using NLog.Targets;

namespace Nethermind.Bootnode;

internal static class LoggingConfigurator
{
    public static void Configure(string logLevel, string? logFile)
    {
        LoggingConfiguration configuration = new();
        NLog.LogLevel level = NLog.LogLevel.FromString(logLevel);

        ColoredConsoleTarget consoleTarget = new("console")
        {
            Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}"
        };
        configuration.AddTarget(consoleTarget);
        configuration.LoggingRules.Add(new LoggingRule("*", level, consoleTarget));

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            string? directory = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            FileTarget fileTarget = new("file")
            {
                FileName = logFile,
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}",
                CreateDirs = true,
                ArchiveFileName = Path.Combine(directory ?? ".", "archive", "bootnode.{#}.log"),
                ArchiveEvery = FileArchivePeriod.Day,
                ArchiveNumbering = ArchiveNumberingMode.Date,
                MaxArchiveFiles = 7
            };
            configuration.AddTarget(fileTarget);
            configuration.LoggingRules.Add(new LoggingRule("*", level, fileTarget));
        }

        LogManager.Configuration = configuration;
        LogManager.ReconfigExistingLoggers();
    }
}

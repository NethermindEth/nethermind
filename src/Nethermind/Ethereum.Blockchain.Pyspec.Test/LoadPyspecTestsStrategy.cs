// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Test;

public class LoadPyspecTestsStrategy : ITestLoadStrategy
{
    // PYSPEC_ARCHIVE_VERSION / PYSPEC_ARCHIVE_NAME let workflow_dispatch jobs (e.g. the
    // FOCIL workflow targeting an unreleased EELS branch) point at a non-default archive
    // without recompiling. Empty/unset envvars fall through to the in-tree defaults.
    public string ArchiveVersion { get; init; } =
        Environment.GetEnvironmentVariable("PYSPEC_ARCHIVE_VERSION") is { Length: > 0 } v ? v : Constants.DEFAULT_ARCHIVE_VERSION;
    public string ArchiveName { get; init; } =
        Environment.GetEnvironmentVariable("PYSPEC_ARCHIVE_NAME") is { Length: > 0 } n ? n : Constants.DEFAULT_ARCHIVE_NAME;

    public IEnumerable<EthereumTest> Load(string testsDir, string wildcard = null)
    {
        string testsDirectoryName = TestFixtureDownloader.EnsureDownloaded(
            "PyTests", Constants.ARCHIVE_URL_TEMPLATE, ArchiveVersion, ArchiveName);

        TestType testType = TestType.Blockchain;
        foreach (TestType type in Enum.GetValues<TestType>())
        {
            if (testsDir.Contains($"{type}_tests", StringComparison.OrdinalIgnoreCase))
            {
                testType = type;
                break;
            }
        }

        IEnumerable<string> directories = !string.IsNullOrEmpty(testsDir)
            ? Directory.EnumerateDirectories(ResolveTestsDirectory(testsDirectoryName, testsDir), "*", new EnumerationOptions { RecurseSubdirectories = true })
            : Directory.EnumerateDirectories(testsDirectoryName, "*", new EnumerationOptions { RecurseSubdirectories = true });
        List<string> testDirs = [];
        foreach (string testDir in directories)
        {
            testDirs.Add(testDir);
        }

        return TestLoadStrategy.LoadTestsFromDirectories(testDirs, wildcard, testType);
    }

    private static string ResolveTestsDirectory(string testsDirectoryName, string testsDir)
    {
        string requestedDirectory = Path.Combine(testsDirectoryName, testsDir);
        if (Directory.Exists(requestedDirectory))
        {
            return requestedDirectory;
        }

        string[] parts = testsDir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        bool hadForkPrefix = false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!parts[i].StartsWith("for_", StringComparison.Ordinal))
            {
                continue;
            }

            parts[i] = parts[i]["for_".Length..];
            hadForkPrefix = true;
        }

        if (hadForkPrefix)
        {
            string legacyDirectory = Path.Combine(testsDirectoryName, Path.Combine(parts));
            if (Directory.Exists(legacyDirectory))
            {
                return legacyDirectory;
            }
        }

        return requestedDirectory;
    }
}

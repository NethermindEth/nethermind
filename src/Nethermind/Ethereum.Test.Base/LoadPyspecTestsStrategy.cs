// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Ethereum.Test.Base;

/// <summary>
/// URL template for downloading pyspec fixture archives from GitHub releases.
/// {0} = version tag, {1} = archive filename.
/// </summary>
public static class PyspecArchive
{
    public const string UrlTemplate = "https://github.com/ethereum/execution-specs/releases/download/{0}/{1}";
}

public class LoadPyspecTestsStrategy : ITestLoadStrategy
{
    public required string ArchiveVersion { get; init; }
    public required string ArchiveName { get; init; }

    public IEnumerable<EthereumTest> Load(string testsDir, string wildcard = null)
    {
        string testsDirectoryName = TestFixtureDownloader.EnsureDownloaded(
            "PyTests", PyspecArchive.UrlTemplate, ArchiveVersion, ArchiveName);

        TestType testType = TestType.Blockchain;
        foreach (TestType type in Enum.GetValues<TestType>())
        {
            if (testsDir.Contains($"{type}_tests", StringComparison.OrdinalIgnoreCase))
            {
                testType = type;
                break;
            }
        }

        string resolvedDir = !string.IsNullOrEmpty(testsDir)
            ? ResolveTestsDirectory(testsDirectoryName, testsDir)
            : testsDirectoryName;

        if (!Directory.Exists(resolvedDir))
            return [];

        IEnumerable<string> directories = Directory.EnumerateDirectories(resolvedDir, "*", new EnumerationOptions { RecurseSubdirectories = true });
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

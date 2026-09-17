// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Test;

public class LoadPyspecTestsStrategy : ITestLoadStrategy
{
    public string ArchiveVersion { get; init; } = Constants.DEFAULT_ARCHIVE_VERSION;
    public string ArchiveName { get; init; } = Constants.DEFAULT_ARCHIVE_NAME;

    public IEnumerable<EthereumTest> Load(string testsDir, string wildcard = null)
    {
        string rootDir = ResolveTestsRoot(testsDir);
        TestType testType = GetTestType(testsDir);

        // Skip absent fork fixtures instead of throwing
        if (!Directory.Exists(rootDir))
            return [];

        List<string> testDirs = [];
        foreach (string testDir in Directory.EnumerateDirectories(rootDir, "*", new EnumerationOptions { RecurseSubdirectories = true }))
        {
            testDirs.Add(testDir);
        }

        return TestLoadStrategy.LoadTestsFromDirectories(testDirs, wildcard, testType);
    }

    /// <summary>
    /// Downloads (if needed) and resolves the fixture root for <paramref name="testsDir"/>.
    /// </summary>
    internal string ResolveTestsRoot(string testsDir)
    {
        string testsDirectoryName = TestFixtureDownloader.EnsureDownloaded(
            "PyTests", Constants.ARCHIVE_URL_TEMPLATE, ArchiveVersion, ArchiveName);

        return !string.IsNullOrEmpty(testsDir)
            ? ResolveTestsDirectory(testsDirectoryName, testsDir)
            : testsDirectoryName;
    }

    internal static TestType GetTestType(string testsDir)
    {
        foreach (TestType type in Enum.GetValues<TestType>())
        {
            if (testsDir.Contains($"{type}_tests", StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return TestType.Blockchain;
    }

    /// <summary>
    /// Enumerates fixture files under <paramref name="rootDir"/> in the same order
    /// <see cref="Load"/> parses them: recursive subdirectories (excluding the root itself),
    /// top-level files per directory.
    /// </summary>
    internal static IEnumerable<(string File, string Directory)> EnumerateTestFiles(string rootDir)
    {
        foreach (string testDir in Directory.EnumerateDirectories(rootDir, "*", new EnumerationOptions { RecurseSubdirectories = true }))
        {
            foreach (string testFile in Directory.EnumerateFiles(testDir))
            {
                yield return (testFile, testDir);
            }
        }
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

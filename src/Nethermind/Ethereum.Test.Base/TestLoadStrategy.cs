// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
namespace Ethereum.Test.Base;

/// <summary>
/// Reusable test load strategy parameterized by root path and test type.
/// Override <see cref="OnTestLoaded"/> or <see cref="HandleLoadFailure"/> for custom behavior.
/// </summary>
public abstract class TestLoadStrategy(string testsRootPath, TestType testType) : ITestLoadStrategy
{
    public IEnumerable<EthereumTest> Load(string testsDirectoryName, string? wildcard = null)
    {
        IEnumerable<string> testDirs;
        if (!Path.IsPathRooted(testsDirectoryName))
        {
            string testsDirectory = GetTestsDirectory();
            testDirs = Directory.EnumerateDirectories(testsDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
        }
        else
        {
            testDirs = [testsDirectoryName];
        }

        List<EthereumTest> tests = [];
        foreach (string testDir in testDirs)
            tests.AddRange(LoadTestsFromDirectoryWithHooks(testDir, wildcard));
        return tests;
    }

    private string GetTestsDirectory()
    {
        string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string root = currentDirectory[..currentDirectory.LastIndexOf("src", StringComparison.Ordinal)];
        return Path.Combine(root, "src", "tests", testsRootPath);
    }

    /// <summary>
    /// Loads all tests from a single directory. Can be called directly by other strategies
    /// that don't need the hook/error-handling infrastructure.
    /// </summary>
    public static List<EthereumTest> LoadTestsFromDirectory(string testDir, string? wildcard, TestType testType)
    {
        List<EthereumTest> testsByName = [];
        foreach (string testFile in Directory.EnumerateFiles(testDir))
        {
            FileTestsSource fileTestsSource = new(testFile, wildcard);
            IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(testType);
            foreach (EthereumTest test in tests)
                test.Category ??= testDir;
            testsByName.AddRange(tests);
        }
        return testsByName;
    }

    private IEnumerable<EthereumTest> LoadTestsFromDirectoryWithHooks(string testDir, string? wildcard)
    {
        List<EthereumTest> testsByName = [];
        foreach (string testFile in Directory.EnumerateFiles(testDir))
        {
            FileTestsSource fileTestsSource = new(testFile, wildcard);
            try
            {
                IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(testType);
                foreach (EthereumTest test in tests)
                {
                    test.Category = testDir;
                    OnTestLoaded(test);
                }
                testsByName.AddRange(tests);
            }
            catch (Exception e)
            {
                EthereumTest? failedTest = HandleLoadFailure(testFile, e);
                if (failedTest is not null)
                    testsByName.Add(failedTest);
            }
        }
        return testsByName;
    }

    /// <summary>Called for each successfully loaded test. Override for post-processing.</summary>
    protected virtual void OnTestLoaded(EthereumTest test) { }

    /// <summary>
    /// Called when a file fails to load. Override to return a placeholder test instead of propagating.
    /// Default behavior re-throws preserving the original stack trace.
    /// </summary>
    protected virtual EthereumTest? HandleLoadFailure(string testFile, Exception e)
    {
        ExceptionDispatchInfo.Capture(e).Throw();
        return null; // unreachable
    }
}

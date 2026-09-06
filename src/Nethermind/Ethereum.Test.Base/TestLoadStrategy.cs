// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Nethermind.Core.Test;

namespace Ethereum.Test.Base;

/// <summary>
/// Reusable test load strategy parameterized by root path and test type.
/// Override <see cref="OnTestLoaded"/> or <see cref="HandleLoadFailure"/> for custom behavior.
/// </summary>
public abstract class TestLoadStrategy(string testsRootPath, TestType testType) : ITestLoadStrategy
{
    private static readonly ParallelOptions LoadParallelOptions = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
    };

    public IEnumerable<EthereumTest> Load(string testsDirectoryName, string? wildcard = null)
    {
        List<string> testDirs = [];
        if (!Path.IsPathRooted(testsDirectoryName))
        {
            string testsDirectory = GetTestsDirectory();
            foreach (string testDir in Directory.EnumerateDirectories(testsDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true }))
            {
                testDirs.Add(testDir);
            }
        }
        else
        {
            testDirs.Add(testsDirectoryName);
        }

        return LoadTestsFromDirectoriesWithHooks(testDirs, wildcard);
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
    public static List<EthereumTest> LoadTestsFromDirectory(string testDir, string? wildcard, TestType testType) =>
        [.. LoadTestsFromDirectories([testDir], wildcard, testType)];

    public static IEnumerable<EthereumTest> LoadTestsFromDirectories(IReadOnlyList<string> testDirs, string? wildcard, TestType testType) =>
        LoadTestFiles(GetTestFiles(testDirs), file =>
        {
            FileTestsSource fileTestsSource = new(file.Path, wildcard);
            IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(testType);
            List<EthereumTest> testsByName = [];
            foreach (EthereumTest test in tests)
            {
                test.Category ??= file.Directory;
                testsByName.Add(test);
            }

            return testsByName;
        });

    private IEnumerable<EthereumTest> LoadTestsFromDirectoriesWithHooks(IReadOnlyList<string> testDirs, string? wildcard) =>
        LoadTestFiles(GetTestFiles(testDirs), file => LoadTestFileWithHooks(file, wildcard));

    private List<EthereumTest> LoadTestFileWithHooks(TestFile file, string? wildcard)
    {
        List<EthereumTest> testsByName = [];
        FileTestsSource fileTestsSource = new(file.Path, wildcard);
        try
        {
            IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(testType);
            foreach (EthereumTest test in tests)
            {
                test.Category = file.Directory;
                OnTestLoaded(test);
                testsByName.Add(test);
            }
        }
        catch (Exception e)
        {
            EthereumTest? failedTest = HandleLoadFailure(file.Path, e);
            if (failedTest is not null)
            {
                testsByName.Add(failedTest);
            }
        }

        return testsByName;
    }

    private static List<TestFile> GetTestFiles(IReadOnlyList<string> testDirs)
    {
        List<TestFile> testFiles = [];
        for (int i = 0; i < testDirs.Count; i++)
        {
            string testDir = testDirs[i];
            foreach (string testFile in Directory.EnumerateFiles(testDir))
            {
                testFiles.Add(new TestFile(testFile, testDir));
            }
        }

        return testFiles;
    }

    private static IEnumerable<EthereumTest> LoadTestFiles(IReadOnlyList<TestFile> testFiles, Func<TestFile, List<EthereumTest>> loadFile)
    {
        if (testFiles.Count == 0)
        {
            return [];
        }

        if (TestChunkFilter.TryGetChunkConfig() is not null)
        {
            return LoadTestFilesSequentially(testFiles, loadFile);
        }

        if (testFiles.Count == 1)
        {
            return loadFile(testFiles[0]);
        }

        return LoadTestFilesInParallel(testFiles, loadFile);
    }

    private static IEnumerable<EthereumTest> LoadTestFilesSequentially(IReadOnlyList<TestFile> testFiles, Func<TestFile, List<EthereumTest>> loadFile)
    {
        for (int i = 0; i < testFiles.Count; i++)
        {
            foreach (EthereumTest test in loadFile(testFiles[i]))
            {
                yield return test;
            }
        }
    }

    private static List<EthereumTest> LoadTestFilesInParallel(IReadOnlyList<TestFile> testFiles, Func<TestFile, List<EthereumTest>> loadFile)
    {
        List<EthereumTest>[] loadedByFile = new List<EthereumTest>[testFiles.Count];
        ExceptionDispatchInfo?[] failures = new ExceptionDispatchInfo?[testFiles.Count];
        Parallel.For(0, testFiles.Count, LoadParallelOptions, i =>
        {
            try
            {
                loadedByFile[i] = loadFile(testFiles[i]);
            }
            catch (Exception e)
            {
                loadedByFile[i] = [];
                failures[i] = ExceptionDispatchInfo.Capture(e);
            }
        });

        for (int i = 0; i < failures.Length; i++)
        {
            failures[i]?.Throw();
        }

        int count = 0;
        for (int i = 0; i < loadedByFile.Length; i++)
        {
            count += loadedByFile[i].Count;
        }

        List<EthereumTest> result = new(count);
        for (int i = 0; i < loadedByFile.Length; i++)
        {
            result.AddRange(loadedByFile[i]);
        }

        return result;
    }

    private readonly record struct TestFile(string Path, string Directory);

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

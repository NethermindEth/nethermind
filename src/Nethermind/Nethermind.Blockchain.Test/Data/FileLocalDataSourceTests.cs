// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Data;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Blockchain.Test.Data;

[Parallelizable(ParallelScope.All)]
public class FileLocalDataSourceTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void correctly_reads_existing_file()
    {
        using TempPath tempFile = TempPath.GetTempFile();
        File.WriteAllText(tempFile.Path, GenerateStringJson("A", "B", "C"));
        // var x = new EthereumJsonSerializer().Serialize(new string []{"A", "B", "C"});
        using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance);
        Assert.That(fileLocalDataSource.Data, Is.EqualTo(new[] { "A", "B", "C" }));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task correctly_updates_from_existing_file()
    {
        using TempPath tempFile = TempPath.GetTempFile();
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A"));
        int interval = 30;
        using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance, interval);
        int changedRaised = 0;
        SemaphoreSlim handle = new(0);
        fileLocalDataSource.Changed += (sender, args) =>
        {
            Interlocked.Increment(ref changedRaised);
            handle.Release();
        };
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("C", "B"));
        await WaitForData(fileLocalDataSource, ["C", "B"], handle);
        Assert.That(changedRaised, Is.GreaterThanOrEqualTo(1));

        int afterFirst = Volatile.Read(ref changedRaised);
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("E", "F"));
        await WaitForData(fileLocalDataSource, ["E", "F"], handle);
        Assert.That(Volatile.Read(ref changedRaised), Is.GreaterThan(afterFirst));
    }

    private static async Task WaitForData(FileLocalDataSource<string[]> source, string[] expected, SemaphoreSlim handle)
    {
        if (!await WaitForCondition(handle, () => source.Data is { } data && data.SequenceEqual(expected)))
            Assert.Fail($"Data did not converge to expected value within {Timeout.MaxWaitTime}ms");
    }

    private static async Task<bool> WaitForCondition(SemaphoreSlim handle, Func<bool> predicate)
    {
        TimeSpan slice = TimeSpan.FromMilliseconds(100);
        TimeSpan budget = TimeSpan.FromMilliseconds(Timeout.MaxWaitTime);
        while (budget > TimeSpan.Zero)
        {
            await handle.WaitAsync(slice);
            if (predicate()) return true;
            budget -= slice;
        }
        return false;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task correctly_updates_from_new_file()
    {
        using TempPath tempFile = TempPath.GetTempFile();
        using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance, 10);
        int changedRaised = 0;
        SemaphoreSlim handle = new(0);
        fileLocalDataSource.Changed += (sender, args) =>
        {
            Interlocked.Increment(ref changedRaised);
            handle.Release();
        };
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A", "B"));
        await WaitForData(fileLocalDataSource, ["A", "B"], handle);
        Assert.That(changedRaised, Is.GreaterThanOrEqualTo(1));
    }

    private static string GenerateStringJson(params string[] items) => $"[{string.Join(", ", items.Select(static i => $"\"{i}\""))}]";

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void loads_default_when_failed_loading_file()
    {
        using TempPath tempFile = TempPath.GetTempFile();
        using (File.Open(tempFile.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance);
            Assert.That(fileLocalDataSource.Data, Is.Null);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    [Ignore("Causing repeated pains on GitHub actions.")]
    public async Task retries_loading_file()
    {
        using TempPath tempFile = TempPath.GetTempFile();
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A", "B", "C"));
        int interval = 30;
        using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance, interval);
        using (FileStream file = File.Open(tempFile.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using (StreamWriter writer = new(file, leaveOpen: true))
            {
                await writer.WriteAsync(GenerateStringJson("A", "B", "C", "D"));
            }

            await Task.Delay(10 * interval);

            Assert.That(fileLocalDataSource.Data, Is.EqualTo(new[] { "A", "B", "C" }));
        }

        await Task.Delay(10 * interval);

        Assert.That(fileLocalDataSource.Data, Is.EqualTo(new[] { "A", "B", "C", "D" }));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task loads_default_when_deleted_file()
    {
        using TempPath tempFile = TempPath.GetTempFile();
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A"));
        using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance, 50);
        int changedRaised = 0;
        SemaphoreSlim handle = new(0);
        fileLocalDataSource.Changed += (sender, args) =>
        {
            Interlocked.Increment(ref changedRaised);
            handle.Release();
        };
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("C", "B"));
        await WaitForData(fileLocalDataSource, ["C", "B"], handle);
        Assert.That(changedRaised, Is.GreaterThanOrEqualTo(1));

        int afterFirst = Volatile.Read(ref changedRaised);
        File.Delete(tempFile.Path);
        await WaitForCondition(handle, () => fileLocalDataSource.Data is null);
        Assert.That(fileLocalDataSource.Data, Is.Null);
        Assert.That(Volatile.Read(ref changedRaised), Is.GreaterThan(afterFirst));
    }
}

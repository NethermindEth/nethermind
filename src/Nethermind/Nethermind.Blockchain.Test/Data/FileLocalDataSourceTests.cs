// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Data;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
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
        // Declared before the data source so it outlives the reload timer: a tick that lands
        // after the semaphore is disposed would throw inside the timer's async void handler.
        using SemaphoreSlim handle = new(0);
        using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance, interval);
        int changedRaised = 0;
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
        using SemaphoreSlim handle = new(0);
        using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance, 10);
        int changedRaised = 0;
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

    private class TestFileSystemStream(Stream stream, string path) : FileSystemStream(stream, path, isAsync: false);

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
    public async Task reloads_file_when_a_later_write_has_an_earlier_local_time()
    {
        // A DST fall-back moves the local write-time representation backwards, so comparing
        // local times makes a genuinely later write look older and suppresses the reload.
        DateTime utcT0 = new(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc);
        MockFileState state = new() { Exists = true, Json = GenerateStringJson("A"), UtcWriteTime = utcT0, LocalWriteTime = utcT0.AddHours(2) };
        using SemaphoreSlim handle = new(0);
        using FileLocalDataSource<string[]> fileLocalDataSource = new("file", new EthereumJsonSerializer(), CreateFileSystem(state), LimboLogs.Instance, 10);
        fileLocalDataSource.Changed += (sender, args) => handle.Release();
        Assert.That(fileLocalDataSource.Data, Is.EqualTo(new[] { "A" }));

        lock (state.Lock)
        {
            state.Json = GenerateStringJson("B");
            state.UtcWriteTime = utcT0.AddSeconds(30);
            state.LocalWriteTime = utcT0.AddHours(1).AddSeconds(30);
        }

        await WaitForData(fileLocalDataSource, ["B"], handle);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task reloads_recreated_file_with_older_write_time()
    {
        // A restore or copy that preserves timestamps recreates the file with a write time
        // older than the deletion instant; the reload must not compare against a wall clock.
        DateTime utcT0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        MockFileState state = new() { Exists = true, Json = GenerateStringJson("A"), UtcWriteTime = utcT0, LocalWriteTime = utcT0 };
        using SemaphoreSlim handle = new(0);
        using FileLocalDataSource<string[]> fileLocalDataSource = new("file", new EthereumJsonSerializer(), CreateFileSystem(state), LimboLogs.Instance, 10);
        fileLocalDataSource.Changed += (sender, args) => handle.Release();
        Assert.That(fileLocalDataSource.Data, Is.EqualTo(new[] { "A" }));

        lock (state.Lock) { state.Exists = false; }
        await WaitForCondition(handle, () => fileLocalDataSource.Data is null);
        Assert.That(fileLocalDataSource.Data, Is.Null, "the deleted file must reset the data");

        lock (state.Lock)
        {
            state.Json = GenerateStringJson("B");
            state.UtcWriteTime = utcT0.AddHours(-1);
            state.LocalWriteTime = utcT0.AddHours(-1);
            state.Exists = true;
        }

        await WaitForData(fileLocalDataSource, ["B"], handle);
    }

    private sealed class MockFileState
    {
        public readonly Lock Lock = new();
        public bool Exists { get; set; }
        public required string Json { get; set; }
        public DateTime UtcWriteTime { get; set; }
        public DateTime LocalWriteTime { get; set; }
    }

    private static IFileSystem CreateFileSystem(MockFileState state)
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Exists(Arg.Any<string>()).Returns(_ => { lock (state.Lock) { return state.Exists; } });
        fileSystem.File.GetLastWriteTime(Arg.Any<string>()).Returns(_ => { lock (state.Lock) { return state.LocalWriteTime; } });
        fileSystem.File.GetLastWriteTimeUtc(Arg.Any<string>()).Returns(_ => { lock (state.Lock) { return state.UtcWriteTime; } });
        fileSystem.File.OpenRead(Arg.Any<string>()).Returns(_ => { lock (state.Lock) { return new TestFileSystemStream(new MemoryStream(Encoding.UTF8.GetBytes(state.Json)), "file"); } });
        return fileSystem;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task loads_default_when_deleted_file()
    {
        using TempPath tempFile = TempPath.GetTempFile();
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A"));
        using SemaphoreSlim handle = new(0);
        using FileLocalDataSource<string[]> fileLocalDataSource = new(tempFile.Path, new EthereumJsonSerializer(), new RealFileSystem(), LimboLogs.Instance, 50);
        int changedRaised = 0;
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

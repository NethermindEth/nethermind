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
        SemaphoreSlim handle = new(0);
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
        SemaphoreSlim handle = new(0);
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
    public async Task reloads_file_when_distinct_utc_write_times_have_the_same_local_time()
    {
        // A DST fold maps two UTC instants onto one local time, so only a local-time collision
        // distinguishes reading the write time in UTC from reading it in local time.
        DateTime utcT0 = new(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc);
        DateTime localFoldTime = new(2026, 10, 25, 1, 30, 0, DateTimeKind.Local);
        MockFileState state = new(new MockFile(Exists: true, GenerateStringJson("A"), utcT0, localFoldTime));
        SemaphoreSlim handle = new(0);
        using FileLocalDataSource<string[]> fileLocalDataSource = new("file", new EthereumJsonSerializer(), CreateFileSystem(state), LimboLogs.Instance, 10);
        fileLocalDataSource.Changed += (sender, args) => handle.Release();
        Assert.That(fileLocalDataSource.Data, Is.EqualTo(new[] { "A" }));

        state.File = state.File with { Json = GenerateStringJson("B"), UtcWriteTime = utcT0.AddHours(1) };

        await WaitForData(fileLocalDataSource, ["B"], handle);
    }

    [TestCase(false, TestName = "reloads_recreated_file_with_unchanged_write_time")]
    [TestCase(true, TestName = "reloads_recreated_default_value_with_unchanged_write_time")]
    [MaxTime(Timeout.MaxTestTime)]
    public async Task reloads_recreated_file_with_unchanged_write_time(bool initialValueIsDefault)
    {
        DateTime utcT0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        string initialJson = initialValueIsDefault ? "null" : GenerateStringJson("A");
        MockFileState state = new(new MockFile(Exists: true, initialJson, utcT0, utcT0));
        SemaphoreSlim handle = new(0);
        using FileLocalDataSource<string[]> fileLocalDataSource = new("file", new EthereumJsonSerializer(), CreateFileSystem(state), LimboLogs.Instance, 10);
        fileLocalDataSource.Changed += (sender, args) => handle.Release();
        Assert.That(fileLocalDataSource.Data, Is.EqualTo(initialValueIsDefault ? null : new[] { "A" }));

        state.File = state.File with { Exists = false };
        Assert.That(await handle.WaitAsync(Timeout.MaxWaitTime), Is.True, "the deletion was not observed");
        Assert.That(fileLocalDataSource.Data, Is.Null, "the deleted file must reset the data");

        state.File = state.File with { Json = GenerateStringJson("B"), Exists = true };

        await WaitForData(fileLocalDataSource, ["B"], handle);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task reloads_atomically_replaced_file_with_older_write_time()
    {
        DateTime utcT0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        MockFileState state = new(new MockFile(Exists: true, GenerateStringJson("A"), utcT0, utcT0));
        SemaphoreSlim handle = new(0);
        using FileLocalDataSource<string[]> fileLocalDataSource = new("file", new EthereumJsonSerializer(), CreateFileSystem(state), LimboLogs.Instance, 10);
        fileLocalDataSource.Changed += (sender, args) => handle.Release();
        Assert.That(fileLocalDataSource.Data, Is.EqualTo(new[] { "A" }));

        state.File = state.File with
        {
            Json = GenerateStringJson("B"),
            UtcWriteTime = utcT0.AddHours(-1),
            LocalWriteTime = utcT0.AddHours(-1),
        };

        await WaitForData(fileLocalDataSource, ["B"], handle);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task skips_a_reload_while_another_one_is_in_progress()
    {
        // Overlapping reloads would publish each other's content and write time. A reload in
        // progress must make the ticks behind it skip the file entirely, not queue up on it.
        const int interval = 10;
        DateTime utcT0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        MockFileState state = new(new MockFile(Exists: false, GenerateStringJson("A"), utcT0, utcT0));
        using ManualResetEventSlim finishRead = new(false);
        SemaphoreSlim readStarted = new(0);
        int reads = 0;
        IFileSystem fileSystem = CreateFileSystem(state, onOpenRead: () =>
        {
            Interlocked.Increment(ref reads);
            readStarted.Release();
            finishRead.Wait();
        });

        // The file appears only after construction, so the blocking read happens on a tick.
        using FileLocalDataSource<string[]> fileLocalDataSource = new("file", new EthereumJsonSerializer(), fileSystem, LimboLogs.Instance, interval);
        SemaphoreSlim handle = new(0);
        fileLocalDataSource.Changed += (sender, args) => handle.Release();
        state.File = state.File with { Exists = true };
        Assert.That(await readStarted.WaitAsync(Timeout.MaxWaitTime), Is.True, "the reload never started");

        await Task.Delay(20 * interval);
        Assert.That(Volatile.Read(ref reads), Is.EqualTo(1), "the ticks behind the reload must skip it");

        finishRead.Set();
        await WaitForData(fileLocalDataSource, ["A"], handle);
    }

    private sealed class AllocatingDefaultFileLocalDataSource(
        string filePath,
        IJsonSerializer jsonSerializer,
        IFileSystem fileSystem,
        ILogManager logManager,
        int interval) : FileLocalDataSource<object>(filePath, jsonSerializer, fileSystem, logManager, interval)
    {
        protected override object DefaultValue => new();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task does_not_report_changes_repeatedly_when_an_allocating_default_file_is_absent()
    {
        MockFileState state = new(new MockFile(Exists: false, Json: "{}"));
        SemaphoreSlim existsChecked = new(0);
        int existsChecks = 0;
        IFileSystem fileSystem = CreateFileSystem(state, () =>
        {
            Interlocked.Increment(ref existsChecks);
            existsChecked.Release();
        });
        using AllocatingDefaultFileLocalDataSource fileLocalDataSource = new("file", new EthereumJsonSerializer(), fileSystem, LimboLogs.Instance, 10);
        object initialData = fileLocalDataSource.Data;
        SemaphoreSlim handle = new(0);
        int changedRaised = 0;
        fileLocalDataSource.Changed += (sender, args) =>
        {
            Interlocked.Increment(ref changedRaised);
            handle.Release();
        };
        int initialExistsChecks = Volatile.Read(ref existsChecks);

        Assert.That(await WaitForCondition(existsChecked, () => Volatile.Read(ref existsChecks) >= initialExistsChecks + 2), Is.True);
        Assert.That(fileLocalDataSource.Data, Is.SameAs(initialData), "an absent file must not replace the data");

        // Corroborating only: the guard is released before Changed is raised, so a stale event
        // could still be in flight. The identity assert above is what catches the regression.
        state.File = state.File with { Exists = true };
        Assert.That(await WaitForCondition(handle, () => Volatile.Read(ref changedRaised) > 0), Is.True);
        Assert.That(Volatile.Read(ref changedRaised), Is.EqualTo(1), "only the load may report a change");
    }

    private sealed record MockFile(bool Exists, string Json, DateTime UtcWriteTime = default, DateTime LocalWriteTime = default);

    private sealed class MockFileState(MockFile file)
    {
        // The test thread swaps the snapshot whole, so a reload never reads a half-applied change.
        private volatile MockFile _file = file;

        public MockFile File { get => _file; set => _file = value; }
    }

    private static IFileSystem CreateFileSystem(MockFileState state, Action? onExists = null, Action? onOpenRead = null)
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Exists(Arg.Any<string>()).Returns(_ =>
        {
            onExists?.Invoke();
            return state.File.Exists;
        });
        fileSystem.File.GetLastWriteTime(Arg.Any<string>()).Returns(_ => state.File.LocalWriteTime);
        fileSystem.File.GetLastWriteTimeUtc(Arg.Any<string>()).Returns(_ => state.File.UtcWriteTime);
        fileSystem.File.OpenRead(Arg.Any<string>()).Returns(_ =>
        {
            onOpenRead?.Invoke();
            return new TestFileSystemStream(new MemoryStream(Encoding.UTF8.GetBytes(state.File.Json)), "file");
        });
        return fileSystem;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task loads_default_when_deleted_file()
    {
        using TempPath tempFile = TempPath.GetTempFile();
        await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A"));
        SemaphoreSlim handle = new(0);
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

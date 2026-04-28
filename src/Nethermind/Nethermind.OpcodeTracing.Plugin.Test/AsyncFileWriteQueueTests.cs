// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Output;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.OpcodeTracing.Plugin.Test;

public class AsyncFileWriteQueueTests
{
    private static PerBlockTraceOutput CreateTrace(long blockNumber) => new()
    {
        Metadata = new PerBlockMetadata { BlockNumber = blockNumber },
        OpcodeCounts = new Dictionary<byte, long> { [0x00] = 1 }
    };

    [Test]
    public void Enqueue_returns_true_for_valid_item()
    {
        PerBlockTraceWriter writer = new(LimboLogs.Instance);
        AsyncFileWriteQueue queue = new(Path.GetTempPath(), writer, LimboLogs.Instance);

        bool result = queue.Enqueue(CreateTrace(1));

        Assert.That(result, Is.True);
    }

    [Test]
    public void Enqueue_returns_false_for_null()
    {
        PerBlockTraceWriter writer = new(LimboLogs.Instance);
        AsyncFileWriteQueue queue = new(Path.GetTempPath(), writer, LimboLogs.Instance);

        bool result = queue.Enqueue(null!);

        Assert.That(result, Is.False);
    }

    [Test]
    public void PendingWrites_increments_on_enqueue()
    {
        PerBlockTraceWriter writer = new(LimboLogs.Instance);
        AsyncFileWriteQueue queue = new(Path.GetTempPath(), writer, LimboLogs.Instance);

        queue.Enqueue(CreateTrace(1));
        queue.Enqueue(CreateTrace(2));
        queue.Enqueue(CreateTrace(3));

        Assert.That(queue.PendingWrites, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task PendingWrites_decrements_after_processing()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nethermind-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            PerBlockTraceWriter writer = new(LimboLogs.Instance);
            await using AsyncFileWriteQueue queue = new(tempDir, writer, LimboLogs.Instance);

            queue.Enqueue(CreateTrace(1));
            queue.Enqueue(CreateTrace(2));

            bool flushed = await queue.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.That(flushed, Is.True);
            Assert.That(queue.PendingWrites, Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task PendingWrites_tracks_correctly_across_many_enqueues()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nethermind-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            PerBlockTraceWriter writer = new(LimboLogs.Instance);
            await using AsyncFileWriteQueue queue = new(tempDir, writer, LimboLogs.Instance);

            for (int i = 0; i < 50; i++)
            {
                queue.Enqueue(CreateTrace(i));
            }

            bool flushed = await queue.FlushAsync(TimeSpan.FromSeconds(10));

            Assert.That(flushed, Is.True);
            Assert.That(queue.PendingWrites, Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Enqueue_returns_false_after_flush()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nethermind-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            PerBlockTraceWriter writer = new(LimboLogs.Instance);
            await using AsyncFileWriteQueue queue = new(tempDir, writer, LimboLogs.Instance);

            await queue.FlushAsync(TimeSpan.FromSeconds(5));

            bool result = queue.Enqueue(CreateTrace(99));

            Assert.That(result, Is.False);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task DisposeAsync_completes_pending_writes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nethermind-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            PerBlockTraceWriter writer = new(LimboLogs.Instance);
            AsyncFileWriteQueue queue = new(tempDir, writer, LimboLogs.Instance);

            queue.Enqueue(CreateTrace(1));
            queue.Enqueue(CreateTrace(2));
            queue.Enqueue(CreateTrace(3));

            await queue.DisposeAsync();

            Assert.That(queue.PendingWrites, Is.EqualTo(0));

            string[] files = Directory.GetFiles(tempDir, "*.json");
            Assert.That(files.Length, Is.EqualTo(3));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void PendingWrites_does_not_increment_for_null_enqueue()
    {
        PerBlockTraceWriter writer = new(LimboLogs.Instance);
        AsyncFileWriteQueue queue = new(Path.GetTempPath(), writer, LimboLogs.Instance);

        queue.Enqueue(null!);

        Assert.That(queue.PendingWrites, Is.EqualTo(0));
    }
}

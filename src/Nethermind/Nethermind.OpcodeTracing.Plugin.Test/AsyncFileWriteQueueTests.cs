// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Output;
using NUnit.Framework;

namespace Nethermind.OpcodeTracing.Plugin.Test;

public class AsyncFileWriteQueueTests
{
    private string _tempDir = null!;
    private AsyncFileWriteQueue _queue = null!;

    private static PerBlockTraceOutput CreateTrace(long blockNumber) => new()
    {
        Metadata = new PerBlockMetadata { BlockNumber = blockNumber },
        OpcodeCounts = new Dictionary<byte, long> { [0x00] = 1 }
    };

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nethermind-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _queue = new AsyncFileWriteQueue(_tempDir, new PerBlockTraceWriter(LimboLogs.Instance), LimboLogs.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        try { await _queue.DisposeAsync(); } catch (ObjectDisposedException) { }
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    [Test]
    public void Enqueue_returns_true_for_valid_item_and_false_for_null()
    {
        Assert.That(_queue.Enqueue(CreateTrace(1)), Is.True);
        Assert.That(_queue.Enqueue(null!), Is.False);
    }

    [Test]
    public void Null_enqueue_does_not_increment_PendingWrites()
    {
        _queue.Enqueue(null!);

        Assert.That(_queue.PendingWrites, Is.EqualTo(0));
    }

    [TestCase(1)]
    [TestCase(50)]
    public async Task PendingWrites_reaches_zero_after_flush(int count)
    {
        for (int i = 0; i < count; i++) _queue.Enqueue(CreateTrace(i));

        bool flushed = await _queue.FlushAsync(TimeSpan.FromSeconds(10));

        Assert.That(flushed, Is.True);
        Assert.That(_queue.PendingWrites, Is.EqualTo(0));
    }

    [Test]
    public async Task Enqueue_returns_false_after_flush()
    {
        await _queue.FlushAsync(TimeSpan.FromSeconds(5));

        Assert.That(_queue.Enqueue(CreateTrace(99)), Is.False);
    }

    [Test]
    public async Task Dispose_writes_all_pending_items_to_disk()
    {
        _queue.Enqueue(CreateTrace(1));
        _queue.Enqueue(CreateTrace(2));
        _queue.Enqueue(CreateTrace(3));

        await _queue.DisposeAsync();

        Assert.That(_queue.PendingWrites, Is.EqualTo(0));
        Assert.That(Directory.GetFiles(_tempDir, "*.json"), Has.Length.EqualTo(3));
    }
}

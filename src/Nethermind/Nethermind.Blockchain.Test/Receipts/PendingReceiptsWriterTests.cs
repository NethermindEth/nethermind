// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[Parallelizable(ParallelScope.Self)]
public class PendingReceiptsWriterTests
{
    private static readonly byte[] Key = [1, 2, 3];
    private static readonly byte[] Value = [4, 5, 6];

    [Test]
    public void Write_is_eventually_flushed_to_the_column()
    {
        MemDb column = new();
        using PendingReceiptsWriter writer = new(column);

        writer.Write(Key, Value, WriteFlags.None);

        Assert.That(() => column.Get(Key), Is.EqualTo(Value).After(5000, 10));
    }

    [Test]
    public void EnsureFlushed_writes_a_buffered_key_synchronously()
    {
        MemDb column = new();
        using PendingReceiptsWriter writer = new(column);

        writer.Write(Key, Value, WriteFlags.None);
        writer.EnsureFlushed(Key);

        Assert.That(column.Get(Key), Is.EqualTo(Value), "the key must be durable in the column once EnsureFlushed returns");
    }

    [Test]
    public void Dispose_drains_pending_writes()
    {
        MemDb column = new();
        PendingReceiptsWriter writer = new(column);

        writer.Write(Key, Value, WriteFlags.None);
        writer.Dispose();

        Assert.That(column.Get(Key), Is.EqualTo(Value));
    }

    [Test]
    public void Drop_then_ensure_flushed_leaves_the_column_untouched()
    {
        MemDb column = new();
        using PendingReceiptsWriter writer = new(column);

        writer.Write(Key, Value, WriteFlags.None);
        writer.Drop(Key);
        writer.EnsureFlushed(Key);

        // Drop under the drain lock removes the entry; whether or not the worker had already flushed it, a
        // subsequent RemoveReceipts also deletes from the column, so a dropped key never resurrects on its own.
        Assert.That(writer.PendingCount, Is.Zero);
    }

    [Test]
    public void Write_after_dispose_goes_straight_to_the_column()
    {
        MemDb column = new();
        PendingReceiptsWriter writer = new(column);
        writer.Dispose();

        writer.Write(Key, Value, WriteFlags.None);

        Assert.That(column.Get(Key), Is.EqualTo(Value));
    }

    [Test]
    public void Flush_worker_survives_a_transient_column_failure()
    {
        ThrowOnceDb column = new();
        using PendingReceiptsWriter writer = new(column);

        writer.Write(Key, Value, WriteFlags.None); // first drain throws inside the worker

        Assert.That(() => column.Get(Key), Is.EqualTo(Value).After(10000, 50), "worker must retry after a failure");
    }

    private sealed class ThrowOnceDb : MemDb
    {
        private int _thrown;

        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (Interlocked.Exchange(ref _thrown, 1) == 0) throw new InvalidOperationException("transient column failure");
            base.Set(key, value, flags);
        }
    }
}

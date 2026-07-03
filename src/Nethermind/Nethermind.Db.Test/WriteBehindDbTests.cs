// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.Self)]
public class WriteBehindDbTests
{
    private static readonly byte[] Key = [1, 2, 3];
    private static readonly byte[] Value = [4, 5, 6];

    [Test]
    public void Set_is_immediately_readable()
    {
        using WriteBehindDb db = new(new MemDb());

        db.Set(Key, Value);

        Assert.That(db.Get(Key), Is.EqualTo(Value));
        Assert.That(db.KeyExists(Key), Is.True);
    }

    [Test]
    public void Set_is_eventually_flushed_to_inner()
    {
        MemDb inner = new();
        using WriteBehindDb db = new(inner);

        db.Set(Key, Value);

        Assert.That(() => inner.Get(Key), Is.EqualTo(Value).After(5000, 10));
        Assert.That(db.Get(Key), Is.EqualTo(Value), "still readable after the background flush");
    }

    [Test]
    public void Remove_hides_the_key_immediately()
    {
        MemDb inner = new();
        inner.Set(Key, Value);
        using WriteBehindDb db = new(inner);

        db.Remove(Key);

        Assert.That(db.Get(Key), Is.Null);
        Assert.That(db.KeyExists(Key), Is.False);
    }

    [Test]
    public void Overwrite_returns_latest_value()
    {
        MemDb inner = new();
        using WriteBehindDb db = new(inner);

        db.Set(Key, [9]);
        db.Set(Key, Value);

        Assert.That(db.Get(Key), Is.EqualTo(Value));
        db.Flush();
        Assert.That(inner.Get(Key), Is.EqualTo(Value));
    }

    [Test]
    public void Flush_drains_synchronously()
    {
        MemDb inner = new();
        using WriteBehindDb db = new(inner);

        db.Set(Key, Value);
        db.Flush();

        Assert.That(inner.Get(Key), Is.EqualTo(Value));
    }

    [Test]
    public void Dispose_drains_pending_writes()
    {
        MemDb inner = new();
        WriteBehindDb db = new(inner);

        db.Set(Key, Value);
        db.Dispose();

        Assert.That(inner.Get(Key), Is.EqualTo(Value));
    }

    [Test]
    public void GetSpan_flushes_the_key_first_so_inner_owns_the_memory()
    {
        MemDb inner = new();
        using WriteBehindDb db = new(inner);

        db.Set(Key, Value);
        Span<byte> span = db.GetSpan(Key);

        Assert.That(span.ToArray(), Is.EqualTo(Value));
        Assert.That(inner.Get(Key), Is.EqualTo(Value), "GetSpan must flush the key to the inner db");
    }

    [Test]
    public void Batch_entries_become_visible_on_dispose()
    {
        MemDb inner = new();
        using WriteBehindDb db = new(inner);

        using (IWriteBatch batch = db.StartWriteBatch())
        {
            batch.Set(Key, Value);
            Assert.That(db.Get(Key), Is.Null, "batch entries are not visible before dispose");
        }

        Assert.That(db.Get(Key), Is.EqualTo(Value));
        Assert.That(() => inner.Get(Key), Is.EqualTo(Value).After(5000, 10));
    }

    [Test]
    public void GetAll_drains_before_enumerating()
    {
        MemDb inner = new();
        using WriteBehindDb db = new(inner);

        db.Set(Key, Value);

        Assert.That(db.GetAll().Select(static kv => kv.Value), Does.Contain(Value));
        Assert.That(inner.Get(Key), Is.EqualTo(Value));
    }

    [Test]
    public void WriteFlags_are_preserved_into_the_inner_write()
    {
        Nethermind.Core.Test.TestMemDb inner = new();
        using WriteBehindDb db = new(inner);

        db.Set(Key, Value, WriteFlags.DisableWAL);
        db.GetSpan(Key); // flushes the key synchronously via FlushKey

        inner.KeyWasWrittenWithFlags(Key, WriteFlags.DisableWAL);
    }

    [Test]
    public void Flush_worker_survives_inner_failures()
    {
        ThrowOnceDb inner = new();
        using WriteBehindDb db = new(inner);

        db.Set(Key, Value); // first drain attempt throws inside the worker

        Assert.That(() => inner.Get(Key), Is.EqualTo(Value).After(10000, 50), "worker must retry after an inner failure");
    }

    [Test]
    public void Overwrites_do_not_leak_the_buffered_size_counter()
    {
        using WriteBehindDb db = new(new MemDb());

        for (int i = 0; i < 1_000; i++) db.Set(Key, Value);

        Assert.That(db.BufferedBytes, Is.LessThanOrEqualTo(Key.Length + Value.Length),
            "replaced entries must not accumulate in the backpressure counter");
    }

    [Test]
    public void Batch_merge_is_not_supported()
    {
        using WriteBehindDb db = new(new MemDb());
        using IWriteBatch batch = db.StartWriteBatch();
        Assert.Throws<NotSupportedException>(() => batch.Merge(Key, Value));
    }

    [Test]
    public void Set_after_dispose_writes_through_to_inner()
    {
        MemDb inner = new();
        WriteBehindDb db = new(inner, disposeInner: false);
        db.Dispose();

        db.Set(Key, Value);

        Assert.That(inner.Get(Key), Is.EqualTo(Value));
    }

    [Test]
    public void Tune_is_passed_through()
    {
        Nethermind.Core.Test.TestMemDb inner = new();
        using WriteBehindDb db = new(inner);

        ((ITunableDb)db).Tune(ITunableDb.TuneType.HeavyWrite);

        Assert.That(inner.WasTunedWith(ITunableDb.TuneType.HeavyWrite), Is.True);
    }

    private sealed class ThrowOnceDb : MemDb
    {
        private int _thrown;

        public override IWriteBatch StartWriteBatch()
            => Interlocked.Exchange(ref _thrown, 1) == 0
                ? throw new InvalidOperationException("transient inner failure")
                : base.StartWriteBatch();
    }

    [Test]
    public void Key_is_always_readable_across_the_flush_transition()
    {
        MemDb inner = new();
        using WriteBehindDb db = new(inner);

        // Regression test for the flush ordering: a key must never be missing from both the buffer
        // and the inner db while the background worker moves it between them.
        const int iterations = 2_000;
        using CancellationTokenSource cts = new();
        Exception? readerFailure = null;

        Task reader = Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        byte[] key = BitConverter.GetBytes(i);
                        byte[]? value = db.Get(key);
                        if (value is not null && !Bytes.AreEqual(value, key))
                        {
                            throw new InvalidOperationException($"unexpected value for {i}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                readerFailure = e;
            }
        });

        for (int i = 0; i < iterations; i++)
        {
            byte[] key = BitConverter.GetBytes(i % 64);
            db.Set(key, key);
            byte[]? read = db.Get(key);
            Assert.That(read, Is.Not.Null, $"key {i % 64} vanished during flush transition (iteration {i})");
        }

        cts.Cancel();
        reader.Wait();
        Assert.That(readerFailure, Is.Null, readerFailure?.ToString());
    }
}

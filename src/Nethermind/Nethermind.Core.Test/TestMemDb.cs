// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Core.Test;

/// <summary>
/// MemDB with additional tools for testing purposes since you can't use NSubstitute with refstruct
/// </summary>
public class TestMemDb : MemDb, ITunableDb, ISortedKeyValueStore
{
    private readonly List<(byte[], ReadFlags)> _readKeys = new();
    private readonly List<((byte[], byte[]?), WriteFlags)> _writes = new();
    private readonly List<byte[]> _removedKeys = new();
    private readonly List<ITunableDb.TuneType> _tuneTypes = new();

    public Func<byte[], byte[]>? ReadFunc { get; set; }
    public Func<byte[], byte[]?, bool>? WriteFunc { get; set; }
    public Action<byte[]>? RemoveFunc { get; set; }

    public bool WasFlushed => FlushCount > 0;
    public int FlushCount { get; set; } = 0;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        _readKeys.Add((key.ToArray(), flags));

        if (ReadFunc is not null) return ReadFunc(key.ToArray());
        return base.Get(key, flags);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _writes.Add(((key.ToArray(), value), flags));

        if (WriteFunc?.Invoke(key.ToArray(), value) == false) return;
        base.Set(key, value, flags);
    }

    public override Span<byte> GetSpan(ReadOnlySpan<byte> key)
    {
        return Get(key);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public override void Remove(ReadOnlySpan<byte> key)
    {
        _removedKeys.Add(key.ToArray());

        if (RemoveFunc is not null)
        {
            RemoveFunc.Invoke(key.ToArray());
            return;
        }
        base.Remove(key);
    }

    public void Tune(ITunableDb.TuneType type)
    {
        _tuneTypes.Add(type);
    }

    public bool WasTunedWith(ITunableDb.TuneType type)
    {
        return _tuneTypes.Contains(type);
    }

    public void KeyWasRead(byte[] key, int times = 1)
    {
        _readKeys.Count(it => Bytes.AreEqual(it.Item1, key)).Should().Be(times);
    }

    public void KeyWasReadWithFlags(byte[] key, ReadFlags flags, int times = 1)
    {
        _readKeys.Count(it => Bytes.AreEqual(it.Item1, key) && it.Item2 == flags).Should().Be(times);
    }

    public void KeyWasWritten(byte[] key, int times = 1)
    {
        _writes.Count(it => Bytes.AreEqual(it.Item1.Item1, key)).Should().Be(times);
    }

    public void KeyWasWritten(Func<(byte[], byte[]?), bool> cond, int times = 1)
    {
        _writes.Count(it => cond.Invoke(it.Item1)).Should().Be(times);
    }

    public void KeyWasWrittenWithFlags(byte[] key, WriteFlags flags, int times = 1)
    {
        _writes.Count(it => Bytes.AreEqual(it.Item1.Item1, key) && it.Item2 == flags).Should().Be(times);
    }

    public void KeyWasRemoved(Func<byte[], bool> cond, int times = 1)
    {
        _removedKeys.Count(cond).Should().Be(times);
    }

    public override IWriteBatch StartWriteBatch()
    {
        return new InMemoryWriteBatch(this);
    }

    public override void Flush(bool onlyWal)
    {
        FlushCount++;
    }

    public byte[]? FirstKey => Keys.Min();
    public byte[]? LastKey => Keys.Max();
    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
    {
        ArrayPoolList<(byte[], byte[]?)> sortedValue = new(1);
        sortedValue.AddRange(GetAll().Select(kv => (kv.Key, kv.Value)));
        sortedValue.AsSpan().Sort((it1, it2) => Bytes.BytesComparer.CompareWithCorrectLength(it1.Item1, it2.Item1));
        return new FakeSortedView(sortedValue);
    }

    private class FakeSortedView(ArrayPoolList<(byte[], byte[]?)> list) : ISortedView
    {
        private int idx = -1;

        public void Dispose()
        {
            list.Dispose();
        }

        public bool StartBefore(ReadOnlySpan<byte> value)
        {
            if (list.Count == 0) return false;

            idx = 0;
            while (idx < list.Count)
            {
                if (Bytes.BytesComparer.CompareWithCorrectLength(list[idx].Item1, value) >= 0)
                {
                    idx--;
                    return true;
                }
                idx++;
            }

            // All keys are less than value - position at last element (largest key <= value)
            idx = list.Count - 1;
            return true;
        }

        public bool MoveNext()
        {
            idx++;
            if (idx >= list.Count) return false;
            return true;
        }

        public ReadOnlySpan<byte> CurrentKey => list[idx].Item1;
        public ReadOnlySpan<byte> CurrentValue => list[idx].Item2;
    }
}

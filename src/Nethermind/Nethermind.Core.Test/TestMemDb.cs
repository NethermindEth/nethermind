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

    public bool WasFlushed => FlushCount > 0;
    public int FlushCount { get; private set; }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        _readKeys.Add((key.ToArray(), flags));
        return ReadFunc is not null ? ReadFunc(key.ToArray()) : base.Get(key, flags);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _writes.Add(((key.ToArray(), value), flags));

        if (WriteFunc?.Invoke(key.ToArray(), value) == false) return;
        base.Set(key, value, flags);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public override void Remove(ReadOnlySpan<byte> key)
    {
        _removedKeys.Add(key.ToArray());
        base.Remove(key);
    }

    public void Tune(ITunableDb.TuneType type) => _tuneTypes.Add(type);
    public bool WasTunedWith(ITunableDb.TuneType type) => _tuneTypes.Contains(type);

    public void KeyWasRead(byte[] key, int times = 1) =>
        _readKeys.Count(it => Bytes.AreEqual(it.Item1, key)).Should().Be(times);

    public void KeyWasReadWithFlags(byte[] key, ReadFlags flags, int times = 1) =>
        _readKeys.Count(it => Bytes.AreEqual(it.Item1, key) && it.Item2 == flags).Should().Be(times);

    public void KeyWasWritten(byte[] key, int times = 1) =>
        _writes.Count(it => Bytes.AreEqual(it.Item1.Item1, key)).Should().Be(times);

    public void KeyWasWritten(Func<(byte[], byte[]?), bool> cond, int times = 1) =>
        _writes.Count(it => cond.Invoke(it.Item1)).Should().Be(times);

    public void KeyWasWrittenWithFlags(byte[] key, WriteFlags flags, int times = 1) =>
        _writes.Count(it => Bytes.AreEqual(it.Item1.Item1, key) && it.Item2 == flags).Should().Be(times);

    public void KeyWasRemoved(Func<byte[], bool> cond, int times = 1) => _removedKeys.Count(cond).Should().Be(times);
    public override IWriteBatch StartWriteBatch() => new InMemoryWriteBatch(this);
    public override void Flush(bool onlyWal) => FlushCount++;

    public byte[]? FirstKey => Keys.Min();
    public byte[]? LastKey => Keys.Max();
    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
    {
        ArrayPoolList<(byte[], byte[]?)> sortedValue = Keys
            .Order(Bytes.Comparer)
            .Select((key) => (key, this.Get(key)))
            .ToPooledList(1);

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
            idx = 0;

            while (idx < list.Count)
            {
                if (Bytes.BytesComparer.Compare(list[idx].Item1, value) >= 0)
                {
                    idx--;
                    return true;
                }
            }

            return false;
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

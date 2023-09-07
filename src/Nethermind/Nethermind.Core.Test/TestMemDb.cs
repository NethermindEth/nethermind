// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Nethermind.Db;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Core.Test;

/// <summary>
/// MemDB with additional tools for testing purposes since you can't use NSubstitute with refstruct
/// </summary>
public class TestMemDb : MemDb, ITunableDb
{
    private List<(byte[], ReadFlags)> _readKeys = new();
    private List<((byte[], byte[]?), WriteFlags)> _writes = new();
    private List<byte[]> _removedKeys = new();
    private List<ITunableDb.TuneType> _tuneTypes = new();

    public Func<byte[], byte[]>? ReadFunc { get; set; }
    public Action<byte[]>? RemoveFunc { get; set; }

    public bool WasFlushed { get; set; }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        _readKeys.Add((key.ToArray(), flags));

        if (ReadFunc != null) return ReadFunc(key.ToArray());
        return base.Get(key, flags);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _writes.Add(((key.ToArray(), value), flags));
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

        if (RemoveFunc != null)
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

    public override IBatch StartBatch()
    {
        return new InMemoryBatch(this);
    }

    public override void Flush()
    {
        WasFlushed = true;
    }
}

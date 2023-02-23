// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Db;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Core.Test;

/// <summary>
/// MemDB with additional tools for testing purposes since you can't use NSubstitute with refstruct
/// </summary>
public class TestMemDb : MemDb
{
    private List<byte[]> _readKeys = new();
    private List<byte[]> _writeKeys = new();
    private List<byte[]> _removedKeys = new();

    public Func<byte[], byte[]>? ReadFunc { get; set; }
    public Action<byte[]>? RemoveFunc { get; set; }

    public override byte[]? this[ReadOnlySpan<byte> key]
    {
        get
        {
            _readKeys.Add(key.ToArray());

            if (ReadFunc != null) return ReadFunc(key.ToArray());
            return base[key];
        }
        set
        {
            _writeKeys.Add(key.ToArray());
            base[key] = value;
        }
    }

    public override Span<byte> GetSpan(ReadOnlySpan<byte> key)
    {
        return this[key];
    }

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

    public void KeyWasRead(byte[] key, int times = 1)
    {
        _readKeys.Count(it => Bytes.AreEqual(it, key)).Should().Be(times);
    }

    public void KeyWasWritten(byte[] key, int times = 1)
    {
        _writeKeys.Count(it => Bytes.AreEqual(it, key)).Should().Be(times);
    }

    public void KeyWasRemoved(Func<byte[], bool> cond, int times = 1)
    {
        _removedKeys.Count(cond).Should().Be(times);
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.SnapServer;

public class PathWithStorageCollector(StorageSlotRanges slots) : RangeQueryVisitor.ILeafValueCollector
{
    public int Collect(in ValueHash256 path, ReadOnlySpan<byte> value)
    {
        slots.AddSlot(in path, value);
        return 32 + value.Length;
    }
}

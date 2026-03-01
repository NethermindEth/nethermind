// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly record struct StorageRead(UInt256 Key) : IComparable<StorageRead>
{
    public int CompareTo(StorageRead other)
        => Key.CompareTo(other.Key);

    public override string ToString() => Key.ToString();
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public struct StorageChange
{
    public ushort BlockAccessIndex { get; set; }
    public byte[] NewValue { get; set; }
}

public struct StorageRead
{
    public byte[] Key { get; set; }
}

public struct BalanceChange
{
    public ushort BlockAccessIndex { get; set; }
    public UInt256 PostBalance { get; set; }
}

public struct NonceChange
{
    public ushort BlockAccessIndex { get; set; }
    public ulong NewNonce { get; set; }
}

public struct CodeChange
{
    public ushort BlockAccessIndex { get; set; }
    public byte[] NewCode { get; set; }
}

public struct SlotChanges()
{
    public byte[] Slot { get; set; } = [];
    public List<StorageChange> Changes { get; set; } = [];
}

public struct AccountChanges(Address address)
{
    public byte[] Address { get; set; } = address.Bytes;
    public SortedDictionary<byte[], SlotChanges> StorageChanges { get; set; } = [];
    public List<StorageRead> StorageReads { get; set; } = [];
    public List<BalanceChange> BalanceChanges { get; set; } = [];
    public List<NonceChange> NonceChanges { get; set; } = [];
    public List<CodeChange> CodeChanges { get; set; } = [];
}

public struct BlockAccessList()
{
    public SortedDictionary<Address, AccountChanges> AccountChanges { get; set; } = [];
}

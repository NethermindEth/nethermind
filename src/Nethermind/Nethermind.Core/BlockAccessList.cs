// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

// # StorageChange: [block_access_index, new_value]
public struct StorageChange
{
    public ushort BlockAccessIndex { get; set; }
    // [SszVector(32)]
    public byte[] NewValue { get; set; }
}

public struct StorageRead
{
    public byte[] Key { get; set; }
}

// BalanceChange: [block_access_index, post_balance]
public struct BalanceChange
{
    public ushort BlockAccessIndex { get; set; }
    public UInt256 PostBalance { get; set; }
}

// NonceChange: [block_access_index, new_nonce]
public struct NonceChange
{
    public ushort BlockAccessIndex { get; set; }
    public ulong NewNonce { get; set; }
}

// CodeChange: [block_access_index, new_code]
public struct CodeChange
{
    public ushort BlockAccessIndex { get; set; }

    // [SszList(Eip7928Constants.MaxCodeSize)]
    public byte[] NewCode { get; set; }
}

// SlotChanges: [slot, [changes]]
// All changes to a single storage slot
public struct SlotChanges()
{
    // [SszVector(32)]
    public byte[] Slot { get; set; } = [];

    // [SszList(Eip7928Constants.MaxTxs)]
    public List<StorageChange> Changes { get; set; } = [];
}

// public struct StorageKey(ReadOnlySpan<byte> key)
// {
//     // [SszVector(32)]
//     public byte[] Key { get; set; } = key.ToArray();
// }

public struct AccountChanges(Address address)
{
    // [SszVector(20)]
    public byte[] Address { get; set; } = address.Bytes;

    // Storage changes (slot -> [tx_index -> new_value])
    // [SszList(Eip7928Constants.MaxSlots)]
    public SortedDictionary<byte[], SlotChanges> StorageChanges { get; set; } = [];

    // Read-only storage keys
    // [SszList(Eip7928Constants.MaxSlots)]
    public List<StorageRead> StorageReads { get; set; } = [];

    // Balance changes ([tx_index -> post_balance])
    // [SszList(Eip7928Constants.MaxTxs)]
    public List<BalanceChange> BalanceChanges { get; set; } = [];

    // Nonce changes ([tx_index -> new_nonce])
    // [SszList(Eip7928Constants.MaxTxs)]
    public List<NonceChange> NonceChanges { get; set; } = [];

    // Code changes ([tx_index -> new_code])
    // [SszList(Eip7928Constants.MaxCodeChanges)]
    public List<CodeChange> CodeChanges { get; set; } = [];
}

public struct BlockAccessList()
{
    // [SszList(Eip7928Constants.MaxAccounts)]
    public SortedDictionary<Address, AccountChanges> AccountChanges { get; set; } = [];
}

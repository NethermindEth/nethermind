// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.BlockAccessLists;

// Single storage write: tx_index -> new_value
public struct StorageChange
{
    public ushort TxIndex { get; set; }
    // [SszVector(32)]
    public byte[] NewValue { get; set; }
}

// Single balance change: tx_index -> post_balance
public struct BalanceChange
{
    public ushort TxIndex { get; set; }
    public UInt128 PostBalance { get; set; }
}

// Single nonce change: tx_index -> new_nonce
public struct NonceChange
{
    public ushort TxIndex { get; set; }
    public ulong NewNonce { get; set; }
}

// Single code change: tx_index -> new_code
public struct CodeChange
{
    public ushort TxIndex { get; set; }

    // [SszList(Eip7928Constants.MaxCodeSize)]
    public byte[] NewCode { get; set; }
}

public struct SlotChanges
{
    // [SszVector(32)]
    public byte[] Slot { get; set; }

    // [SszList(Eip7928Constants.MaxTxs)]
    public ulong Changes { get; set; }
}

public struct StorageKey
{
    // [SszVector(32)]
    public byte[] Key { get; set; }
}

public struct AccountChanges
{
    // [SszVector(20)]
    public byte[] Address { get; set; }

    // Storage changes (slot -> [tx_index -> new_value])
    // [SszList(Eip7928Constants.MaxSlots)]
    public List<StorageChange> StorageChanges { get; set;  }

    // Read-only storage keys
    // [SszList(Eip7928Constants.MaxSlots)]
    public List<StorageKey> StorageReads { get; set;  }

    // Balance changes ([tx_index -> post_balance])
    // [SszList(Eip7928Constants.MaxTxs)]
    public List<BalanceChange> BalanceChanges { get; set;  }

    // Nonce changes ([tx_index -> new_nonce])
    // [SszList(Eip7928Constants.MaxTxs)]
    public List<NonceChange> NonceChanges { get; set;  }

    // Code changes ([tx_index -> new_code])
    // [SszList(Eip7928Constants.MaxCodeChanges)]
    public List<CodeChange> CodeChanges { get; set;  }
}

public struct BlockAccessList
{
    // [SszList(Eip7928Constants.MaxAccounts)]
    public Dictionary<Address, AccountChanges> AccountChanges { get; set;  }

    // RLP encode bal
    public byte[] Bytes => [];
}

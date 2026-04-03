// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.Parallel;

public class TransactionStateDiff
{
    // Read sets (for conflict detection) — excludes coinbase
    public HashSet<AddressAsKey> ReadAccounts { get; } = [];
    public HashSet<StorageCell> ReadStorageCells { get; } = [];

    // Write sets (for applying diff + conflict detection) — excludes coinbase
    public Dictionary<AddressAsKey, (Account? Before, Account? After)> AccountWrites { get; } = [];
    public Dictionary<StorageCell, byte[]> StorageWrites { get; } = [];
    public List<(Address Address, ValueHash256 CodeHash, byte[] Code)> CodeWrites { get; } = [];

    // Coinbase handled as a special accumulator (excluded from conflict detection)
    public UInt256 CoinbaseBalanceDelta { get; set; }
}

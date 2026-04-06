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

    // Raw write batch entries (for scope-level application)
    public List<(Address Address, Account? Account)> AccountWrites { get; } = [];
    public List<(Address Address, UInt256 Index, byte[] Value)> StorageWrites { get; } = [];
    public List<(ValueHash256 CodeHash, byte[] Code)> CodeWrites { get; } = [];

    // Write addresses for conflict detection — excludes coinbase
    public HashSet<AddressAsKey> WrittenAccounts { get; } = [];
    public HashSet<StorageCell> WrittenStorageCells { get; } = [];

}

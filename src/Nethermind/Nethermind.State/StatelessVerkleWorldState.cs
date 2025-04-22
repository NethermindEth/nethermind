// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.State;

public class StatelessVerkleWorldState(
    FrozenDictionary<Address, Account>? systemAccounts,
    IKeyValueStoreWithBatching? codeDb,
    ILogManager logManager)
    : VerkleWorldState(new VerkleStateTree(new NullVerkleTreeStore(), logManager), codeDb, logManager, null, false)
{
    protected override Account? GetAndAddToCache(Address address)
    {
        if (systemAccounts is null || !systemAccounts.TryGetValue(address, out Account? account))
        {
            account = base.GetAndAddToCache(address);
        }

        return account;
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.State;

public class StatelessVerkleWorldState(
    FrozenDictionary<Address, Account>? systemAccounts,
    IKeyValueStore? codeDb,
    ILogManager logManager)
    : VerkleWorldState(new VerkleStateTree(new NullVerkleTreeStore(), logManager), codeDb, logManager)
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

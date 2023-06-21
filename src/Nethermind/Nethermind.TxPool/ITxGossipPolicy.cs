// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.TxPool;

public interface ITxGossipPolicy
{
    bool CanGossipTransactions { get; }
    bool ShouldGossipTransaction(Transaction tx) => CanGossipTransactions;
}

public class CompositeTxGossipPolicy : ITxGossipPolicy
{
    public List<ITxGossipPolicy> Policies { get; } = new();
    public bool CanGossipTransactions => Policies.All(static p => p.CanGossipTransactions);
    public bool ShouldGossipTransaction(Transaction tx) => Policies.All(p => p.ShouldGossipTransaction(tx));
}

public class ShouldGossip : ITxGossipPolicy
{
    public static readonly ShouldGossip Instance = new();
    public bool CanGossipTransactions => true;
}

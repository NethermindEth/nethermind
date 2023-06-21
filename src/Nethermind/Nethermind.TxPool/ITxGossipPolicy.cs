// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool;

public interface ITxGossipPolicy
{
    bool CanGossipTransactions { get; }
    bool ShouldGossipTransaction(Transaction tx) => CanGossipTransactions;
}

public class CompositeTxGossipPolicy : ITxGossipPolicy
{
    public ITxGossipPolicy TxGossipPolicy { get; set; } = ShouldGossip.Instance;
    public bool CanGossipTransactions => TxGossipPolicy.CanGossipTransactions;
    public bool ShouldGossipTransaction(Transaction tx) => TxGossipPolicy.ShouldGossipTransaction(tx);
}

public class ShouldGossip : ITxGossipPolicy
{
    public static readonly ShouldGossip Instance = new();
    public bool CanGossipTransactions => true;
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool;

public interface ITxGossipPolicy
{
    bool ShouldListenToGossipedTransactions => true;
    bool CanGossipTransactions => true;
    bool ShouldGossipTransaction(Transaction tx) => true;
}

// Lazy: resolving ITxGossipPolicy[] eagerly pulls in the sync infrastructure
// (SyncedTxGossipPolicy → ISyncModeSelector → ...) which depends on services
// not yet available during init step construction.
public class CompositeTxGossipPolicy(Lazy<ITxGossipPolicy[]> policies) : ITxGossipPolicy
{
    public bool ShouldListenToGossipedTransactions
    {
        get
        {
            ITxGossipPolicy[] policy = policies.Value;
            for (int i = 0; i < policy.Length; i++)
            {
                if (!policy[i].ShouldListenToGossipedTransactions)
                    return false;
            }
            return true;
        }
    }

    public bool CanGossipTransactions
    {
        get
        {
            ITxGossipPolicy[] policy = policies.Value;
            for (int i = 0; i < policy.Length; i++)
            {
                if (!policy[i].CanGossipTransactions)
                    return false;
            }
            return true;
        }
    }

    public bool ShouldGossipTransaction(Transaction tx)
    {
        ITxGossipPolicy[] policy = policies.Value;
        for (int i = 0; i < policy.Length; i++)
        {
            if (!policy[i].ShouldGossipTransaction(tx))
                return false;
        }
        return true;
    }
}

public class ShouldGossip : ITxGossipPolicy
{
    public static readonly ShouldGossip Instance = new();
}

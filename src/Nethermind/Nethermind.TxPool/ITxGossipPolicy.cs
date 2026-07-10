// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public interface ITxGossipPolicy
{
    bool ShouldListenToGossipedTransactions => true;
    bool CanGossipTransactions => true;
    bool ShouldGossipTransaction(Transaction tx) => true;
}

public interface ITxGossipPolicySource
{
    ITxGossipPolicy[] Policies { get; }
}

// The source is deliberately lazy: resolving ITxGossipPolicy[] eagerly pulls in the sync infrastructure
// (SyncedTxGossipPolicy -> ISyncModeSelector -> ...) which depends on services not yet available during
// init step construction.
public class CompositeTxGossipPolicy(ITxGossipPolicySource policySource) : ITxGossipPolicy
{
    public bool ShouldListenToGossipedTransactions
    {
        get
        {
            ITxGossipPolicy[] policy = policySource.Policies;
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
            ITxGossipPolicy[] policy = policySource.Policies;
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
        ITxGossipPolicy[] policy = policySource.Policies;
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

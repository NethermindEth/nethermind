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
            ITxGossipPolicy[] p = policies.Value;
            for (int i = 0; i < p.Length; i++)
            {
                if (!p[i].ShouldListenToGossipedTransactions)
                    return false;
            }
            return true;
        }
    }

    public bool CanGossipTransactions
    {
        get
        {
            ITxGossipPolicy[] p = policies.Value;
            for (int i = 0; i < p.Length; i++)
            {
                if (!p[i].CanGossipTransactions)
                    return false;
            }
            return true;
        }
    }

    public bool ShouldGossipTransaction(Transaction tx)
    {
        ITxGossipPolicy[] p = policies.Value;
        for (int i = 0; i < p.Length; i++)
        {
            if (!p[i].ShouldGossipTransaction(tx))
                return false;
        }
        return true;
    }
}

public class ShouldGossip : ITxGossipPolicy
{
    public static readonly ShouldGossip Instance = new();
}

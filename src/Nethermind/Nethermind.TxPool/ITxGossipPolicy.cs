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

public class CompositeTxGossipPolicy(params ITxGossipPolicy[] policies) : ITxGossipPolicy
{
    public bool ShouldListenToGossipedTransactions
    {
        get
        {
            for (int i = 0; i < policies.Length; i++)
            {
                if (!policies[i].ShouldListenToGossipedTransactions)
                    return false;
            }
            return true;
        }
    }

    public bool CanGossipTransactions
    {
        get
        {
            for (int i = 0; i < policies.Length; i++)
            {
                if (!policies[i].CanGossipTransactions)
                    return false;
            }
            return true;
        }
    }

    public bool ShouldGossipTransaction(Transaction tx)
    {
        for (int i = 0; i < policies.Length; i++)
        {
            if (!policies[i].ShouldGossipTransaction(tx))
                return false;
        }
        return true;
    }
}

public class ShouldGossip : ITxGossipPolicy
{
    public static readonly ShouldGossip Instance = new();
}

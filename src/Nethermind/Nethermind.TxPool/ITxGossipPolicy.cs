// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;

namespace Nethermind.TxPool;

public interface ITxGossipPolicy
{
    bool ShouldListenToGossipedTransactions => true;
    bool CanGossipTransactions => true;
    bool ShouldGossipTransaction(Transaction tx) => true;
}

public class CompositeTxGossipPolicy : ITxGossipPolicy
{
    public List<ITxGossipPolicy> Policies { get; } = new();
    public bool ShouldListenToGossipedTransactions
    {
        get
        {
            foreach (ITxGossipPolicy policy in CollectionsMarshal.AsSpan(Policies))
            {
                if (!policy.ShouldListenToGossipedTransactions)
                {
                    return false;
                }
            }
            return true;
        }
    }

    public bool CanGossipTransactions
    {
        get
        {
            foreach (ITxGossipPolicy policy in CollectionsMarshal.AsSpan(Policies))
            {
                if (!policy.CanGossipTransactions)
                {
                    return false;
                }
            }
            return true;
        }
    }

    public bool ShouldGossipTransaction(Transaction tx)
    {
        foreach (ITxGossipPolicy policy in CollectionsMarshal.AsSpan(Policies))
        {
            if (!policy.ShouldGossipTransaction(tx))
            {
                return false;
            }
        }
        return true;
    }
}

public class ShouldGossip : ITxGossipPolicy
{
    public static readonly ShouldGossip Instance = new();
}

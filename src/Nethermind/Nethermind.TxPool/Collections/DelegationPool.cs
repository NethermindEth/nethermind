// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.TxPool.Collections;
public class DelegationPool : SortedPool<UInt256, Transaction, AddressAsKey>
{
    public DelegationPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager) : base(capacity, comparer, logManager)
    {
    }

    protected override IComparer<Transaction> GetGroupComparer(IComparer<Transaction> comparer)
    {
        throw new NotImplementedException();
    }

    protected override UInt256 GetKey(Transaction value)
    {
        throw new NotImplementedException();
    }

    protected override IComparer<Transaction> GetUniqueComparer(IComparer<Transaction> comparer)
    {
        throw new NotImplementedException();
    }

    protected override AddressAsKey MapToGroup(Transaction value)
    {
        throw new NotImplementedException();
    }
}

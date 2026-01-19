// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcTxGossipPolicy(ISpecProvider provider) : ITxGossipPolicy
{
    public bool ShouldGossipTransaction(Transaction tx)
    {
        var spec = (IXdcReleaseSpec)provider.GetFinalSpec();

        return !tx.RequiresSpecialHandling(spec);
    }
}

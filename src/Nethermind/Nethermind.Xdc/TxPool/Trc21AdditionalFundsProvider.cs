// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using System.Collections.Generic;

namespace Nethermind.Xdc.TxPool;

internal sealed class Trc21AdditionalFundsProvider(
    IBlockTree blockTree,
    ISpecProvider specProvider,
    ITrc21StateReader trc21StateReader) : IAdditionalFundsProvider
{
    public UInt256 GetAdditionalFunds(Transaction tx)
    {
        if (tx.To is null)
            return UInt256.Zero;

        XdcBlockHeader currentHead = (XdcBlockHeader)blockTree.Head?.Header;
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(currentHead!);
        if (!spec.IsTipTrc21FeeEnabled)
            return UInt256.Zero;

        Dictionary<Address, UInt256> feeCapacities = trc21StateReader.GetFeeCapacities(currentHead);
        return feeCapacities.TryGetValue(tx.To, out UInt256 capacity)
            ? capacity
            : UInt256.Zero;
    }
}

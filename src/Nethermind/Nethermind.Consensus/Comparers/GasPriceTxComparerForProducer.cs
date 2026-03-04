// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Comparers
{
    /// <summary>Block producer knows what will be base fee of next block. We can extract it from blockPreparationContextService and
    /// use to order transactions</summary>
    public class GasPriceTxComparerForProducer(
        BlockPreparationContext blockPreparationContext,
        ISpecProvider specProvider)
        : IComparer<Transaction>
    {
        public int Compare(Transaction? x, Transaction? y)
        {
            bool isEip1559Enabled = specProvider.GetSpecFor1559(blockPreparationContext.BlockNumber).IsEip1559Enabled;
            return GasPriceTxComparerHelper.Compare(x, y, blockPreparationContext.BaseFee, isEip1559Enabled);
        }
    }
}

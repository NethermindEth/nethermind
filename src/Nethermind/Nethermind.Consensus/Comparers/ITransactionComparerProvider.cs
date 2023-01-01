// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Comparers
{
    public interface ITransactionComparerProvider
    {
        IComparer<Transaction> GetDefaultComparer();

        IComparer<Transaction> GetDefaultProducerComparer(BlockPreparationContext blockPreparationContext);
    }
}

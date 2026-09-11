// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IInclusionListSatisfactionChecker"/>
public sealed class InclusionListSatisfactionChecker(ISpecProvider specProvider, ITxValidator txValidator)
    : IInclusionListSatisfactionChecker
{
    public bool IsSatisfied(Block processedBlock, Block suggestedBlock, IWorldState worldState)
    {
        IReleaseSpec spec = specProvider.GetSpec(processedBlock.Header);
        // P2P-decoded blocks legitimately have null IL; IsSatisfied treats null as "not applicable".
        return InclusionListValidator.IsSatisfied(processedBlock, suggestedBlock.InclusionListTransactions, worldState, spec, txValidator);
    }
}

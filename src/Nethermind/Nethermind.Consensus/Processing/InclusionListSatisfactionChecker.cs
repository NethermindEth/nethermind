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
    public bool IsSatisfied(Block block, IReadOnlyStateProvider postTransactionsState)
    {
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        // P2P-decoded blocks legitimately have null IL; IsSatisfied treats null as "not applicable".
        return InclusionListValidator.IsSatisfied(block, postTransactionsState, spec, txValidator);
    }
}

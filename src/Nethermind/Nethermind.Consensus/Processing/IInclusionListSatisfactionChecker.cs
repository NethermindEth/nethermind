// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <summary>Reports whether a processed block honoured the inclusion list it was suggested with (EIP-7805).</summary>
/// <remarks>Not block validation: an unsatisfied inclusion list leaves the block valid and committed. Runs
/// inside block processing because appendability is judged against the state after the block's transactions,
/// before its withdrawals and post-execution system calls.</remarks>
public interface IInclusionListSatisfactionChecker
{
    bool IsSatisfied(Block block, IReadOnlyStateProvider postTransactionsState);
}

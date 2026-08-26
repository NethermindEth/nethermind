// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Transactions;

/// <summary>Feeds the inclusion list supplied by <c>engine_forkchoiceUpdatedV5</c> into block production (EIP-7805).</summary>
public interface IInclusionListTxSource : ITxSource
{
    /// <summary>Retains the list for the build identified by the <paramref name="inclusionListTransactions"/>
    /// array instance, which is the key <c>GetTransactions</c> looks it up by.</summary>
    void Set(byte[][] inclusionListTransactions, IReleaseSpec spec);
}

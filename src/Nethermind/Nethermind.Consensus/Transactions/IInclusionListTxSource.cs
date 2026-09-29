// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Transactions;

/// <summary>Feeds the inclusion list supplied by <c>engine_forkchoiceUpdatedV5</c> into block production (EIP-7805).</summary>
public interface IInclusionListTxSource : ITxSource
{
    /// <summary>Retains the list for the build identified by the <paramref name="inclusionListTransactions"/>
    /// array instance, which is the key <c>GetTransactions</c> looks it up by.</summary>
    /// <param name="inclusionListMembership">One well-formed membership entry per transaction, or <c>null</c>
    /// when none was sent (EIP-8369).</param>
    void Set(byte[][] inclusionListTransactions, IReleaseSpec spec, byte[][]? inclusionListMembership = null);

    /// <summary>The build's EIP-8369 Profile 2 candidates that an omission would leave enforceable, so the
    /// build answers each it fails to append with a claim; <c>null</c> when there are none.</summary>
    /// <remarks>With a membership only the entries the per-IL VERIFY budget fill admits qualify.</remarks>
    IReadOnlySet<Hash256AsKey>? GetProfile2Candidates(PayloadAttributes? payloadAttributes, ulong maxVerifyGasPerTx);
}

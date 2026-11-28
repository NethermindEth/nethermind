// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Transactions;

/// <summary>Feeds the inclusion lists supplied by <c>engine_forkchoiceUpdatedV5</c> into block production (EIP-7805).</summary>
public interface IInclusionListTxSource : ITxSource
{
    /// <summary>Retains the separate lists for the build identified by the <paramref name="inclusionLists"/>
    /// array instance, which is the key <c>GetTransactions</c> looks them up by.</summary>
    /// <param name="inclusionLists">One structurally valid list per committee position (<see cref="Core.InclusionLists"/>).</param>
    void Set(byte[][][] inclusionLists, IReleaseSpec spec);

    /// <summary>The build's Profile 2 candidates that some committee position's VERIFY budget fill admits, so the
    /// build answers each it fails to append with an omission claim; <c>null</c> when there are none.</summary>
    IReadOnlySet<Hash256AsKey>? GetProfile2Candidates(PayloadAttributes? payloadAttributes, ulong maxVerifyGasPerTx);
}

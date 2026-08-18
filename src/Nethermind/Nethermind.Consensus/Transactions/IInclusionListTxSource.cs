// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Transactions;

/// <summary>
/// EIP-7805 (FOCIL): the transaction source that feeds the inclusion list supplied by
/// <c>engine_forkchoiceUpdatedV5</c> into block production.
/// </summary>
public interface IInclusionListTxSource : ITxSource
{
    /// <summary>Decodes and retains the inclusion list for the build identified by <paramref name="inclusionListTransactions"/>.</summary>
    /// <param name="inclusionListTransactions">The RLP-encoded inclusion-list entries, as received in the payload attributes.</param>
    /// <param name="spec">The spec at the build's timestamp.</param>
    void Set(byte[][] inclusionListTransactions, IReleaseSpec spec);
}

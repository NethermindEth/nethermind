// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Answers an already-committed block's EIP-7805 inclusion-list compliance.</summary>
/// <remarks>
/// Lets <c>engine_forkchoiceUpdatedV5</c> derive <c>inclusionListSatisfied</c> for a <c>VALID</c> head from
/// the retained list when the head's <c>engine_newPayloadV6</c> never produced an answer
/// (<see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/bogota.md">bogota.md</see>).
/// </remarks>
public interface IInclusionListComplianceEvaluator
{
    /// <summary>Evaluates compliance of the block identified by <paramref name="blockHash"/> against
    /// <paramref name="inclusionListTransactions"/>, without re-executing it.</summary>
    /// <param name="blockHash">Hash of a block this node has committed.</param>
    /// <param name="inclusionListTransactions">Aggregate inclusion list as RLP-encoded EIP-2718 entries.</param>
    /// <returns><c>null</c> when the block is unknown or its state is no longer readable.</returns>
    bool? TryEvaluate(Hash256 blockHash, byte[][] inclusionListTransactions);
}

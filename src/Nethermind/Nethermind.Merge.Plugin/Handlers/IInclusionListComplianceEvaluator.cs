// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
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
    /// <returns><c>null</c> when the block, state, or execution gas dimensions needed to judge compliance are unavailable.</returns>
    bool? TryEvaluate(Hash256 blockHash, byte[][] inclusionListTransactions);

    /// <summary>Evaluates the counted lists, each binding its dependency-bearing entries only under a non-null EIP-8288 commitment.</summary>
    bool? TryEvaluate(Hash256 blockHash, byte[][] inclusionListTransactions, IReadOnlyList<InclusionListV1> lists);

    /// <summary>Evaluates the counted lists with the EIP-8288 obligations resolved when the payload arrived.</summary>
    /// <param name="heldProofs">Whether this node held a valid proof for each list's commitment then, as
    /// <see cref="ResolveHeldProofs"/> returned; held proofs are released at their deadlines, so a later resolution could
    /// excuse omissions newPayload would have reported.</param>
    bool? TryEvaluate(Hash256 blockHash, byte[][] inclusionListTransactions, IReadOnlyList<InclusionListV1> lists, bool[]? heldProofs)
        => TryEvaluate(blockHash, inclusionListTransactions, lists);

    /// <summary>Resolves, for each list, whether this node holds a valid proof for its EIP-8288 commitment under the fork of
    /// the payload at <paramref name="blockNumber"/> and <paramref name="timestamp"/>.</summary>
    /// <returns><c>null</c> when the lists cannot be resolved.</returns>
    bool[]? ResolveHeldProofs(IReadOnlyList<InclusionListV1> lists, ulong blockNumber, ulong timestamp) => null;
}

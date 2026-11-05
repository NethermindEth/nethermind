// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>Decodes a settled batch's DA stream into what derivation rebuilds blocks from.</summary>
public interface IBatchDerivation
{
    /// <exception cref="EezSettlementException">The stream does not decode, belongs to another rollup or covers no blocks.</exception>
    DecodedBatch Decode(ReadOnlyMemory<byte> daStream);
}

/// <summary>A batch's DA, decoded: the blocks it covers and how they are rebuilt.</summary>
public abstract class DecodedBatch
{
    /// <summary>How many consecutive L2 blocks the batch covers, its Sync block last.</summary>
    public abstract ulong BlockCount { get; }

    /// <summary>The blocks the batch settles, with the Sync block carrying a system transaction per effect L1 applied.</summary>
    /// <param name="applied">The effects L1 applied, over the outbound effects followed by the inbound ones.</param>
    /// <exception cref="EezSettlementException">The DA cannot rebuild the blocks it claims.</exception>
    public abstract DerivedBlock[] Derive(ProducingSlice applied, in DerivationInputs inputs);

    public virtual ValueTask<DerivedBlock[]> DeriveAsync(ProducingSlice applied, DerivationInputs inputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return new(Derive(applied, in inputs));
    }
}

/// <summary>What a batch's blocks derive from besides its DA.</summary>
/// <param name="SystemNonce">The system account's nonce at the parent of the first rebuilt block.</param>
/// <param name="PrevRandao">The seed in effect after the previous settled endpoint, carried by every rebuilt block.</param>
/// <param name="CandidateParent">
/// The block the batch's whole range builds on, below the first rebuilt block when a competing batch made its first steps.
/// </param>
/// <param name="Settling">The L1 block that settled the batch.</param>
/// <param name="Resumed">
/// A competing batch already committed the Sync block, so only the transactions this batch appends to it are rebuilt.
/// </param>
public readonly record struct DerivationInputs(ulong SystemNonce, Hash256 PrevRandao, BlockHeader CandidateParent, L1Anchor Settling, bool Resumed)
{
    public BlockHeader? ExecutionParent { get; init; }
    public ulong AnchorSelector { get; init; } = ulong.MaxValue;
}

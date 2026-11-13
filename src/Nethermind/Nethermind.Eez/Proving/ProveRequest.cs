// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Proving;

/// <summary>A window to prove: the blocks it settles, each with its witness, and the batch that settles them, without proofs.</summary>
public sealed record ProveRequest(ulong RollupId, ulong FromBlock, ulong ToBlock, PostBatch Batch, IReadOnlyList<ProvedBlock> Blocks)
{
    /// <summary>The request one attester is sent: the batch names only its own proof system, the shape it signs.</summary>
    public ProveRequest For(Address proofSystem) => this with
    {
        Batch = Batch with
        {
            ProofSystems = [proofSystem],
            RollupIdsWithProofSystems = [new RollupProofSystems(RollupId, [0])],
            Proofs = [],
        },
    };
}

public sealed record ProvedBlock(ulong Number, Hash256 Hash, Hash256 ParentHash, byte[] Rlp, Witness Witness);

/// <summary>A proof of one attester, for the proof system it signs for.</summary>
public readonly record struct Attestation(Address ProofSystem, byte[] Proof);

public enum ProveFailureKind
{
    /// <summary>The attester is unavailable, busy or late; the same request may succeed later.</summary>
    Retryable,

    /// <summary>The attester refuses one effect of the batch; composing without it may succeed.</summary>
    Actionable,

    /// <summary>The attester refuses the batch as it is.</summary>
    Backend,
}

/// <summary>The effect an attester refuses: an outbound user transaction of the settling block, or an inbound entry of the batch.</summary>
public readonly record struct RefusedEffect(bool Outbound, uint Index, ValueHash256 Hash);

/// <summary>An attester did not return a valid proof.</summary>
public sealed class ProveException(ProveFailureKind kind, string message, RefusedEffect? refused = null) : Exception(message)
{
    public ProveFailureKind Kind { get; } = kind;

    public RefusedEffect? Refused { get; } = refused;
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>Verification status of a block's execution payload, as reported by the execution layer.</summary>
/// <remarks>
/// Mirrors Lighthouse's <c>ExecutionStatus</c> enum. Lighthouse embeds the execution block hash in
/// each variant; here the hash lives separately in <see cref="ProtoNode.ExecutionBlockHash"/>
/// (<c>null</c> if and only if the status is <see cref="Irrelevant"/>).
/// </remarks>
public enum ExecutionStatus
{
    /// <summary>The block is pre-merge, or post-merge but before the terminal PoW block; it has no payload to verify.</summary>
    Irrelevant,

    /// <summary>The execution layer has not yet verified the payload.</summary>
    Optimistic,

    /// <summary>The execution layer has determined the payload is valid.</summary>
    Valid,

    /// <summary>The execution layer has determined the payload is invalid.</summary>
    Invalid,
}

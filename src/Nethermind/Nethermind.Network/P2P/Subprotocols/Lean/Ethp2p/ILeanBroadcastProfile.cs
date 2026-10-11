// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>Whether a manifest's producer holds the duty it claims.</summary>
public enum LeanBroadcastAuthorization
{
    /// <summary>Signature, fork digest and duty check out against authenticated consensus context.</summary>
    Authorized,

    /// <summary>The consensus context is unknown or unresolved: bounded deferral or retrieval, never early forwarding.</summary>
    Unresolved,

    /// <summary>Bad signature, wrong fork or no such duty: the peer that forwarded it skipped the required checks.</summary>
    Rejected
}

/// <summary>A network's EIP-8437 broadcast profile: producer duties, their signatures and the slot clock.</summary>
/// <remarks>
/// EIP-8437 requires these parameters to be fixed by the network before activation and never taken from peers: the
/// <see cref="Id"/>, canonical signature and authorization encodings, the signature relation and domain, consensus-context
/// authentication, the slot clock and producer-duty rules (proposer or selected builder for kind 2, the inclusion-list
/// committee position for kind 3). A node without a registered profile uses retrieval only.
/// </remarks>
public interface ILeanBroadcastProfile
{
    /// <summary>The 32-byte <c>broadcast_profile_id</c> identifying exactly this configuration.</summary>
    ValueHash256 Id { get; }

    /// <summary>The current slot of the profile's slot clock.</summary>
    ulong CurrentSlot { get; }

    /// <summary>When <paramref name="slot"/> starts under the profile's slot clock.</summary>
    DateTimeOffset SlotStart(ulong slot);

    /// <summary>Checks the producer's signature over <paramref name="manifestId"/>, the fork digest and the claimed duty.</summary>
    LeanBroadcastAuthorization Authorize(LeanBroadcastManifest manifest, in ValueHash256 manifestId, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> authorization);

    /// <summary>Whether a reconstructed body matches the block or ordered inclusion list of the manifest's consensus context.</summary>
    bool MatchesContext(LeanBroadcastManifest manifest, ReadOnlySpan<byte> body);
}

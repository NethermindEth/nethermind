// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.ProofAggregation;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76;

/// <summary>EIP-8518 message bounds and this client's local caps.</summary>
public static class Eth76Protocol
{
    public const int MessageIdSpaceSize = Eth76MessageCode.ProofWrapperManifests + 1;

    /// <summary>Wrappers one <c>NewProofWrapperHashes</c> announces.</summary>
    public const int MaxAnnouncedWrappers = 64;

    /// <summary>The largest <c>NewProofWrapperHashes</c> payload: 64 entries of the longest length and three hashes.</summary>
    public const int MaxAnnouncementBytes = 6_723;

    /// <summary>Hashes one <c>GetProofWrappers</c> or <c>GetProofWrapperManifests</c> requests.</summary>
    public const int MaxRequestedWrappers = 32;

    /// <summary>The EIP-8518 recommended soft limit of a <c>ProofWrappers</c> or <c>ProofWrapperManifests</c> response (2 MiB).</summary>
    public const int SoftResponseLimit = 2 * 1024 * 1024;

    /// <summary><c>GetProofWrappers</c> and <c>GetProofWrapperManifests</c> requests served per second per peer.</summary>
    public const int MaxServedRequestsPerSecond = 4;

    /// <summary>How long a peer's requests may queue before further ones are answered empty.</summary>
    public static readonly TimeSpan MaxServeBacklog = TimeSpan.FromSeconds(1);

    /// <summary>The largest valid response, checked before decompression: the soft limit, which the last entry may cross by
    /// one complete wrapper and its hash, plus the request ID and list prefixes.</summary>
    public const int LocalMaxResponseBytes = SoftResponseLimit + ProofWrapperCodec.MaxWrapperBytes + 64;

    /// <summary>Whether an advertised uncompressed size exceeds this client's cap for an <c>eth/76</c> message, checked before
    /// decompression.</summary>
    public static bool ExceedsMessageLimit(int code, int uncompressedLength) => code switch
    {
        Eth76MessageCode.NewProofWrapperHashes => uncompressedLength > MaxAnnouncementBytes,
        Eth76MessageCode.ProofWrappers or Eth76MessageCode.ProofWrapperManifests => uncompressedLength > LocalMaxResponseBytes,
        _ => false,
    };
}

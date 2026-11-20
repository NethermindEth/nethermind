// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Synchronization.Blocks;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V74;

/// <summary>EIP-8498 message bounds and this client's local caps.</summary>
public static class Eth74Protocol
{
    /// <summary>One past the last message ID, <c>DependencyProofs</c>.</summary>
    public const int MessageIdSpaceSize = Eth74MessageCode.DependencyProofs + 1;

    /// <summary>The EIP-8498 recommended soft limit of a <c>DependencyProofs</c> response (2 MiB).</summary>
    /// <remarks>A responder stops adding proofs once the response reaches it; a single proof may exceed it.</remarks>
    public const int SoftResponseLimit = Eth73Protocol.SoftResponseLimit;

    /// <summary>Hashes per <c>GetDependencyProofs</c>, an implementation-defined limit.</summary>
    public const int MaxProofsPerRequest = BlocksRequest.MaxDependencyProofsPerRequest;

    /// <summary><c>GetDependencyProofs</c> requests served per second per peer, at most
    /// <see cref="SoftResponseLimit"/> of proofs each.</summary>
    public const int MaxServedRequestsPerSecond = 4;

    /// <summary>How long a peer's requests may queue before further ones are answered with no proofs.</summary>
    public static readonly TimeSpan MaxServeBacklog = TimeSpan.FromSeconds(1);
}

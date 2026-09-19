// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>A dialable beacon chain peer produced by discv5 discovery.</summary>
/// <param name="Multiaddress">Libp2p multiaddr including the <c>/p2p/&lt;peer-id&gt;</c> component, for example <c>/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2...</c>.</param>
/// <param name="PeerId">The base58 libp2p peer id derived from the ENR's secp256k1 key.</param>
/// <param name="ForkDigest">The 4-byte fork digest the peer advertised in its <c>eth2</c> ENR entry.</param>
/// <param name="EnrSequence">The sequence number of the ENR the candidate was built from.</param>
public sealed record BeaconPeerCandidate(string Multiaddress, string PeerId, byte[] ForkDigest, ulong EnrSequence);

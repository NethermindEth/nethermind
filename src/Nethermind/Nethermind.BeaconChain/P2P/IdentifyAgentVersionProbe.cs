// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using IdentifyMessage = Nethermind.Libp2p.Protocols.Identify.Dto.Identify;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// The session's one <c>/ipfs/id/1.0.0</c> dial: it checks the peer's identity and records the answer in the peer store,
/// and also returns <c>agentVersion</c>, the client string the Beacon API's <c>node/peers</c> surface reports, which the
/// library's own dial reads and then discards.
/// </summary>
/// <remarks>
/// Shares the identify protocol id, so it must never answer an inbound identify request differently
/// from the library: <see cref="ListenAsync"/> hands the stream to the real protocol instance.
/// </remarks>
public sealed class IdentifyAgentVersionProbe(IdentifyProtocol identify, IdentifyProtocolSettings settings, PeerStore peerStore) : ISessionProtocol<object?, string?>
{
    /// <summary>Also bounds the whole identify dial in <see cref="BeaconP2P"/>: every admission waits on this answer.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The largest identify answer read; a peer's declared length is checked before any of the body is buffered.</summary>
    internal const int MaxMessageSize = 8 * 1024;

    public string Id => identify.Id;

    public async Task<string?> DialAsync(IChannel downChannel, ISessionContext context, object? request)
    {
        using CancellationTokenSource cts = new(ReadTimeout);
        int length = await downChannel.ReadVarintAsync(cts.Token);
        // A read of length 0 takes everything the peer has pending, so it would lift the bound; an empty answer carries no key anyway.
        if (length == 0)
        {
            throw new PeerConnectionException("Identify answer declares no bytes");
        }

        if ((uint)length > MaxMessageSize)
        {
            throw new PeerConnectionException($"Identify answer of {(uint)length} bytes exceeds {MaxMessageSize}");
        }

        IdentifyMessage message = IdentifyMessage.Parser.ParseFrom(await downChannel.ReadAsync(length, ReadBlockingMode.WaitAll, cts.Token).OrThrow());
        return Apply(message, context.State, settings, peerStore);
    }

    public Task ListenAsync(IChannel downChannel, ISessionContext context) => identify.ListenAsync(downChannel, context);

    /// <summary>Verifies an identify answer against the session's authenticated remote key and records it in the peer store.</summary>
    /// <returns>The answer's <c>agentVersion</c>, or <c>null</c> when it carries none.</returns>
    /// <exception cref="PeerConnectionException">The answer names another key, or its signed peer record fails the configured policy.</exception>
    /// <remarks>As the library's identify dial: protocols always replace the stored ones, a record is stored only when it verifies and is newer, and only
    /// <see cref="PeerRecordsVerificationPolicy.RequireCorrect"/> refuses an absent or invalid record. Unlike it, nothing reaches discovery, whose pubsub dials bypass the peer band.</remarks>
    internal static string? Apply(IdentifyMessage message, Libp2p.Core.State remote, IdentifyProtocolSettings settings, PeerStore peerStore)
    {
        PublicKey remoteKey = remote.RemotePublicKey ?? throw new PeerConnectionException("Identify before the remote key is authenticated");
        PeerId remotePeerId = remote.RemotePeerId ?? throw new PeerConnectionException("Identify before the remote peer id is known");
        if (remoteKey.ToByteString() != message.PublicKey)
        {
            throw new PeerConnectionException("Malformed peer identity: the remote public key corresponds to a different peer id");
        }

        bool verified;
        ulong seq;
        try
        {
            verified = SigningHelper.VerifyPeerRecord(message.SignedPeerRecord, remoteKey, out seq);
        }
        catch (InvalidProtocolBufferException)
        {
            // A record that does not parse is an unverified one, as in the library's identify dial.
            verified = false;
            seq = 0;
        }

        if (!verified && settings.PeerRecordsVerificationPolicy == PeerRecordsVerificationPolicy.RequireCorrect)
        {
            throw new PeerConnectionException("Malformed peer identity: peer record signature is not valid");
        }

        PeerStore.PeerInfo peerInfo = peerStore.GetPeerInfo(remotePeerId);
        peerInfo.SupportedProtocols = [.. message.Protocols];
        // Pubsub peer exchange hands out the stored record, so an unverified one is never stored.
        if (verified && !(peerInfo.Seq >= seq))
        {
            peerInfo.SignedPeerRecord = message.SignedPeerRecord;
            peerInfo.Seq = seq;
        }

        return message.HasAgentVersion ? message.AgentVersion : null;
    }
}

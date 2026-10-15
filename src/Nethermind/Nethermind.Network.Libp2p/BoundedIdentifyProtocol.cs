// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using IdentifyMessage = Nethermind.Libp2p.Protocols.Identify.Dto.Identify;

namespace Nethermind.Network.Libp2p;

/// <summary>Identifies a peer with a bounded response size and deadline.</summary>
public sealed class BoundedIdentifyProtocol(IProtocolStackSettings stack, PeerStore peerStore, IdentifyProtocolSettings? settings = null,
    Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null, IdentifyProtocol? identify = null) : ISessionProtocol
{
    private readonly IdentifyProtocol _identify = identify ?? new(stack, settings, peerStore, loggerFactory);
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    internal const int MaxMessageSize = 8 * 1024;
    internal const int MaxStoredProtocols = 64;
    internal const int MaxStoredProtocolIdBytes = 128;
    public string Id => ((IProtocol)_identify).Id;

    public async Task DialAsync(IChannel downChannel, ISessionContext context) =>
        Apply(await ReadMessageAsync(downChannel), context.State, settings ?? new IdentifyProtocolSettings(), peerStore);

    /// <summary>Reads one length-prefixed identify message of at most <see cref="MaxMessageSize"/> bytes within <see cref="ReadTimeout"/>.</summary>
    internal static async Task<IdentifyMessage> ReadMessageAsync(IChannel downChannel)
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

        return IdentifyMessage.Parser.ParseFrom(await downChannel.ReadAsync(length, ReadBlockingMode.WaitAll, cts.Token).OrThrow());
    }

    public Task ListenAsync(IChannel downChannel, ISessionContext context) => ((ISessionListenerProtocol)_identify).ListenAsync(downChannel, context);

    internal static string? Apply(IdentifyMessage message, Nethermind.Libp2p.Core.State remote, IdentifyProtocolSettings settings, PeerStore peerStore)
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
        peerInfo.SupportedProtocols = StoredProtocols(message.Protocols);
        // Pubsub peer exchange hands out the stored record, so an unverified one is never stored.
        if (verified && !(peerInfo.Seq >= seq))
        {
            peerInfo.SignedPeerRecord = message.SignedPeerRecord;
            peerInfo.Seq = seq;
            peerStore.Discover(message.SignedPeerRecord);
        }

        return message.HasAgentVersion ? message.AgentVersion : null;
    }

    /// <summary>The first <see cref="MaxStoredProtocols"/> of <paramref name="protocols"/> no longer than <see cref="MaxStoredProtocolIdBytes"/> bytes each.</summary>
    /// <remarks>The library's peer store never removes an entry, so what one entry holds is bounded instead.</remarks>
    public static string[] StoredProtocols(IEnumerable<string> protocols)
    {
        List<string> stored = [];
        foreach (string protocol in protocols)
        {
            if (Encoding.UTF8.GetByteCount(protocol) <= MaxStoredProtocolIdBytes)
            {
                stored.Add(protocol);
                if (stored.Count == MaxStoredProtocols) break;
            }
        }

        return [.. stored];
    }
}

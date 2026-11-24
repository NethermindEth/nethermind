// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V75;
using Nethermind.Network.P2P.Subprotocols.Eth.V75.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

/// <summary><c>eth/76</c> <c>NewProofWrapperHashes (0x18) = [[mode, length, hash, deps-hash, txs-hash, txs-meta], ...]</c>: the
/// <c>eth/75</c> message with coverage hints, for wrappers of either mode.</summary>
public sealed class NewProofWrapperHashesMessage76(WrapperAnnouncement[] announcements) : NewProofWrapperHashesMessage(announcements);

/// <summary><c>eth/76</c> <c>GetProofWrappers (0x19) = [request-id, compact, [hash, ...]]</c>.</summary>
public sealed class GetProofWrappersMessage76(long requestId, bool compact, ValueHash256[] hashes) : GetProofWrappersMessage(requestId, compact, hashes);

/// <summary><c>GetProofWrapperManifests (0x1b) = [request-id, [hash, ...]]</c>: 1 to 32 distinct hashes.</summary>
public sealed class GetProofWrapperManifestsMessage(long requestId, ValueHash256[] hashes) : Eth75Message
{
    public override int PacketType => Eth76MessageCode.GetProofWrapperManifests;
    public long RequestId { get; } = requestId;
    public ValueHash256[] Hashes { get; } = hashes;
}

/// <summary>A ProofWrapperManifests entry <c>[hash, txs-meta]</c>: a wrapper's hash and the metadata of all its transactions.</summary>
public readonly record struct ProofWrapperManifest(ValueHash256 Hash, WrapperMetadata Manifest);

/// <summary><c>ProofWrapperManifests (0x1c) = [request-id, [[hash, txs-meta], ...]]</c>.</summary>
public sealed class ProofWrapperManifestsMessage(long requestId, ProofWrapperManifest[] manifests) : Eth75Message
{
    public override int PacketType => Eth76MessageCode.ProofWrapperManifests;
    public long RequestId { get; } = requestId;
    public ProofWrapperManifest[] Manifests { get; } = manifests;
}

public sealed class NewProofWrapperHashesMessage76Serializer()
    : Eth75MessageSerializer<NewProofWrapperHashesMessage76>(Eth76Protocol.MaxNewProofWrapperHashesBytes)
{
    public override byte[] Encode(NewProofWrapperHashesMessage76 message)
    {
        byte[][] items = new byte[message.Announcements.Length][];
        for (int i = 0; i < items.Length; i++) items[i] = message.Announcements[i].Encoded76;
        return Eth75Rlp.EncodeList(items);
    }

    protected override NewProofWrapperHashesMessage76 Decode(ref Eth75RlpReader fields) => new(ReadAnnouncements(ref fields, WrapperAnnouncement.Decode76));
}

public sealed class GetProofWrappersMessage76Serializer() : Eth75MessageSerializer<GetProofWrappersMessage76>(Eth76Protocol.MaxGetProofWrappersBytes)
{
    public override byte[] Encode(GetProofWrappersMessage76 message) =>
        Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt((ulong)message.RequestId), Eth75Rlp.EncodeUInt(message.Compact ? 1UL : 0UL), EncodeHashes(message.Hashes));

    protected override GetProofWrappersMessage76 Decode(ref Eth75RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        ulong compact = fields.ReadUInt64();
        if (compact > 1) throw new RlpException("The compact flag must be 0 or 1");
        return new(requestId, compact == 1, ReadRequestedHashes(ref fields));
    }
}

public sealed class GetProofWrapperManifestsMessageSerializer()
    : Eth75MessageSerializer<GetProofWrapperManifestsMessage>(Eth76Protocol.MaxGetProofWrapperManifestsBytes)
{
    public override byte[] Encode(GetProofWrapperManifestsMessage message) =>
        Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt((ulong)message.RequestId), EncodeHashes(message.Hashes));

    protected override GetProofWrapperManifestsMessage Decode(ref Eth75RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        return new(requestId, ReadRequestedHashes(ref fields));
    }
}

public sealed class ProofWrapperManifestsMessageSerializer() : Eth75MessageSerializer<ProofWrapperManifestsMessage>(Eth76Protocol.LocalMaxManifestsBytes)
{
    public override byte[] Encode(ProofWrapperManifestsMessage message)
    {
        byte[][] entries = new byte[message.Manifests.Length][];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(message.Manifests[i].Hash.Bytes), message.Manifests[i].Manifest.Encoded);
        return Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt((ulong)message.RequestId), Eth75Rlp.EncodeList(entries));
    }

    /// <remarks>Manifests have no 17-transaction limit; each must fit a wrapper within <c>MAX_WRAPPER_BYTES</c>, and the
    /// requester checks it against the announced length and <c>txs-hash</c>.</remarks>
    protected override ProofWrapperManifestsMessage Decode(ref Eth75RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        Eth75RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth75Protocol.MaxWrappersPerRequest);
        ProofWrapperManifest[] manifests = new ProofWrapperManifest[count];
        for (int i = 0; i < count; i++)
        {
            Eth75RlpReader entry = list.ReadList();
            ValueHash256 hash = entry.ReadHash();
            manifests[i] = new(hash, WrapperMetadata.Decode(entry.ReadItem(), Eth75Protocol.MaxWrapperBytes, int.MaxValue));
            entry.End();
        }
        list.End();
        return new(requestId, manifests);
    }
}

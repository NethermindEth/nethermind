// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>A producer-signed broadcast manifest committing an object and its 16-of-32 coded shards (EIP-8437 ethp2p).</summary>
/// <remarks>
/// <code>
/// manifest = [broadcast_profile_id, fork_digest, slot, duty_index, producer_index,
///             consensus_context_root, descriptor, auxiliary, coding]
/// coding = [16, 16, shard_bytes, body_sha256, shard_hashes]
/// </code>
/// Decoding checks the encoding and coding geometry, before any coder is initialized; it does not authorize the producer.
/// </remarks>
public sealed class LeanBroadcastManifest
{
    public const int MaxPreambleBytes = 65536;
    public const int ForkDigestBytes = 4;
    private static readonly byte[] ManifestDomain = [.. "lean/1/broadcast"u8, 0];
    private static readonly byte[] ChannelDomain = [.. "lean/1/channel"u8, 0];
    public const string ChannelPrefix = "lean/1/rs/";

    private LeanBroadcastManifest(byte[] encoded, ValueHash256 broadcastProfileId, byte[] forkDigest, ulong slot, ulong dutyIndex,
        ulong producerIndex, ValueHash256 consensusContextRoot, LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton, int shardBytes,
        ValueHash256 bodySha256, ValueHash256[] shardHashes)
    {
        Encoded = encoded;
        BroadcastProfileId = broadcastProfileId;
        ForkDigest = forkDigest;
        Slot = slot;
        DutyIndex = dutyIndex;
        ProducerIndex = producerIndex;
        ConsensusContextRoot = consensusContextRoot;
        Descriptor = descriptor;
        Skeleton = skeleton;
        ShardBytes = shardBytes;
        BodySha256 = bodySha256;
        ShardHashes = shardHashes;
    }

    /// <summary>The canonical RLP encoding of the manifest list.</summary>
    public byte[] Encoded { get; }
    public ValueHash256 BroadcastProfileId { get; }
    public byte[] ForkDigest { get; }
    public ulong Slot { get; }
    public ulong DutyIndex { get; }
    public ulong ProducerIndex { get; }
    public ValueHash256 ConsensusContextRoot { get; }
    public LeanDescriptor Descriptor { get; }

    /// <summary>The kind-2 header skeleton carried as <c>auxiliary</c>; null for kind 3.</summary>
    public LeanHeaderSkeleton? Skeleton { get; }
    public int ShardBytes { get; }
    public ValueHash256 BodySha256 { get; }
    public ValueHash256[] ShardHashes { get; }

    /// <summary>Builds the manifest of a fully validated body, encoding it to commit every shard.</summary>
    public static LeanBroadcastManifest Create(in ValueHash256 broadcastProfileId, ReadOnlySpan<byte> forkDigest, ulong slot, ulong dutyIndex,
        ulong producerIndex, in ValueHash256 consensusContextRoot, LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton, ReadOnlySpan<byte> body,
        out byte[][] shards)
    {
        shards = LeanReedSolomon.Encode(body);
        byte[][] shardHashes = new byte[LeanReedSolomon.TotalShards][];
        for (int i = 0; i < shardHashes.Length; i++) shardHashes[i] = LeanRlp.EncodeBytes(SHA256.HashData(shards[i]));
        byte[] coding = LeanRlp.EncodeList(LeanRlp.EncodeUInt(LeanReedSolomon.DataShards), LeanRlp.EncodeUInt(LeanReedSolomon.DataShards),
            LeanRlp.EncodeUInt((ulong)shards[0].Length), LeanRlp.EncodeBytes(SHA256.HashData(body)), LeanRlp.EncodeList(shardHashes));
        byte[] encoded = LeanRlp.EncodeList(LeanRlp.EncodeBytes(broadcastProfileId.Bytes), LeanRlp.EncodeBytes(forkDigest), LeanRlp.EncodeUInt(slot),
            LeanRlp.EncodeUInt(dutyIndex), LeanRlp.EncodeUInt(producerIndex), LeanRlp.EncodeBytes(consensusContextRoot.Bytes), descriptor.Encoded,
            skeleton?.Encoded ?? LeanRlp.EncodeBytes([]), coding);
        return Decode(encoded);
    }

    /// <summary>Decodes a manifest, checking its fields and coding geometry.</summary>
    /// <exception cref="RlpException">The manifest is malformed or its geometry is not the profile's.</exception>
    public static LeanBroadcastManifest Decode(ReadOnlySpan<byte> encoded)
    {
        LeanRlpReader outer = new(encoded);
        LeanRlpReader fields = outer.ReadList();
        outer.End();
        return Read(ref fields, encoded);
    }

    private static LeanBroadcastManifest Read(ref LeanRlpReader fields, ReadOnlySpan<byte> encoded)
    {
        ValueHash256 broadcastProfileId = fields.ReadHash();
        ReadOnlySpan<byte> forkDigest = fields.ReadBytes();
        if (forkDigest.Length != ForkDigestBytes) throw new RlpException("Fork digest must be 4 bytes");
        ulong slot = fields.ReadUInt64();
        ulong dutyIndex = fields.ReadUInt64();
        ulong producerIndex = fields.ReadUInt64();
        ValueHash256 contextRoot = fields.ReadHash();
        LeanDescriptor descriptor = LeanDescriptor.Decode(fields.ReadItem());
        ReadOnlySpan<byte> auxiliary = fields.ReadItem();
        LeanRlpReader coding = fields.ReadList();
        fields.End();

        LeanHeaderSkeleton? skeleton;
        switch (descriptor.Kind)
        {
            case LeanProtocol.KindBlockProof:
                skeleton = LeanHeaderSkeleton.Decode(auxiliary);
                break;
            case LeanProtocol.KindInclusionList:
                LeanRlpReader empty = new(auxiliary);
                empty.ReadEmptyBytes();
                empty.End();
                skeleton = null;
                break;
            default:
                throw new RlpException("Only kinds 2 and 3 are broadcast");
        }
        if (descriptor.ByteLength > LeanReedSolomon.MaxBodyBytes) throw new RlpException("Broadcast body exceeds 2**20 bytes");
        if (coding.ReadUInt64() != LeanReedSolomon.DataShards || coding.ReadUInt64() != LeanReedSolomon.DataShards)
            throw new RlpException("Broadcast coding is not 16 data and 16 parity shards");
        ulong shardBytes = coding.ReadUInt64();
        if (shardBytes != (ulong)LeanReedSolomon.ShardBytes((int)descriptor.ByteLength)) throw new RlpException("Shard width does not match the body length");
        ValueHash256 bodySha256 = coding.ReadHash();
        LeanRlpReader hashList = coding.ReadList();
        if (hashList.CountRemaining(LeanReedSolomon.TotalShards) != LeanReedSolomon.TotalShards) throw new RlpException("Coding needs exactly 32 shard hashes");
        ValueHash256[] shardHashes = new ValueHash256[LeanReedSolomon.TotalShards];
        for (int i = 0; i < shardHashes.Length; i++) shardHashes[i] = hashList.ReadHash();
        hashList.End();
        coding.End();
        return new LeanBroadcastManifest(encoded.ToArray(), broadcastProfileId, forkDigest.ToArray(), slot, dutyIndex, producerIndex, contextRoot,
            descriptor, skeleton, (int)shardBytes, bodySha256, shardHashes);
    }

    /// <summary><c>H("lean/1/broadcast\0" || RLP([chain_id, genesis_hash, manifest]))</c>.</summary>
    public ValueHash256 Id(in UInt256 chainId, in ValueHash256 genesisHash) =>
        ValueKeccak.Compute([.. ManifestDomain, .. LeanRlp.EncodeList(LeanRlp.EncodeUInt256(chainId), LeanRlp.EncodeBytes(genesisHash.Bytes), Encoded)]);

    /// <summary>The framework <c>message_id</c>: the lowercase hex manifest ID.</summary>
    public static string MessageId(in ValueHash256 manifestId) => manifestId.Bytes.ToHexString(false);

    /// <summary><c>"lean/1/rs/" || lowerhex(H("lean/1/channel\0" || RLP([chain_id, genesis_hash, broadcast_profile_id, kind, profile_id])))</c>.</summary>
    public static string Channel(in UInt256 chainId, in ValueHash256 genesisHash, in ValueHash256 broadcastProfileId, byte kind, in ValueHash256 profileId) =>
        ChannelPrefix + ValueKeccak.Compute([.. ChannelDomain, .. LeanRlp.EncodeList(LeanRlp.EncodeUInt256(chainId), LeanRlp.EncodeBytes(genesisHash.Bytes),
            LeanRlp.EncodeBytes(broadcastProfileId.Bytes), LeanRlp.EncodeUInt(kind), LeanRlp.EncodeBytes(profileId.Bytes))]).Bytes.ToHexString(false);

    public string Channel(in UInt256 chainId, in ValueHash256 genesisHash) =>
        Channel(chainId, genesisHash, BroadcastProfileId, Descriptor.Kind, Descriptor.ProfileId);

    /// <summary><c>preamble = RLP([manifest, signature, authorization])</c>, at most 65,536 bytes.</summary>
    public byte[] Preamble(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> authorization)
    {
        byte[] preamble = LeanRlp.EncodeList(Encoded, LeanRlp.EncodeBytes(signature), LeanRlp.EncodeBytes(authorization));
        if (preamble.Length > MaxPreambleBytes) throw new ArgumentException("Preamble exceeds 65,536 bytes");
        return preamble;
    }

    /// <summary>Decodes a preamble into its manifest, signature and authorization.</summary>
    /// <exception cref="RlpException">The preamble is oversized or malformed.</exception>
    public static LeanBroadcastManifest DecodePreamble(ReadOnlySpan<byte> preamble, out byte[] signature, out byte[] authorization)
    {
        if (preamble.Length > MaxPreambleBytes) throw new RlpException("Preamble exceeds 65,536 bytes");
        LeanRlpReader outer = new(preamble);
        LeanRlpReader fields = outer.ReadList();
        outer.End();
        ReadOnlySpan<byte> manifest = fields.ReadItem();
        signature = fields.ReadBytes().ToArray();
        authorization = fields.ReadBytes().ToArray();
        fields.End();
        return Decode(manifest);
    }

    /// <summary>Whether a shard has the committed width and SHA-256 digest.</summary>
    public bool IsCommittedShard(int index, ReadOnlySpan<byte> shard) =>
        (uint)index < LeanReedSolomon.TotalShards && shard.Length == ShardBytes && SHA256.HashData(shard).AsSpan().SequenceEqual(ShardHashes[index].Bytes);

    /// <summary>Reconstructs and checks the body from 16 distinct checked shards.</summary>
    /// <param name="codeword">All 32 re-encoded shards when the body is returned.</param>
    /// <returns>The body, or null with <paramref name="error"/> when the shards do not form the committed codeword.</returns>
    /// <remarks>Checks zero padding, <c>body_sha256</c> and, by re-encoding, all 32 shard hashes. The caller still checks the
    /// descriptor's canonical commitments and the kind's validity.</remarks>
    public byte[]? Reconstruct(ReadOnlySpan<int> indices, byte[][] shards, out byte[][]? codeword, out string? error)
    {
        codeword = null;
        byte[] padded = LeanReedSolomon.Reconstruct(indices, shards);
        int length = (int)Descriptor.ByteLength;
        if (padded.AsSpan(length).ContainsAnyExcept((byte)0))
        {
            error = "nonzero padding";
            return null;
        }
        byte[] body = padded[..length];
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(BodySha256.Bytes))
        {
            error = "body SHA-256 mismatch";
            return null;
        }
        byte[][] rebuilt = LeanReedSolomon.Encode(body);
        for (int i = 0; i < rebuilt.Length; i++)
            if (!SHA256.HashData(rebuilt[i]).AsSpan().SequenceEqual(ShardHashes[i].Bytes))
            {
                error = "noncanonical parity commitment";
                return null;
            }
        codeword = rebuilt;
        error = null;
        return body;
    }
}

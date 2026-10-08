// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>EIP-8437 kind-2 <c>skeleton = [proof_field_index, field_encodings]</c>.</summary>
/// <remarks>
/// Each entry is one top-level header field's complete RLP, except the empty <c>recursive_stark</c> placeholder.
/// The skeleton is never hashed, validated or stored as a header itself.
/// </remarks>
public sealed class LeanHeaderSkeleton
{
    private LeanHeaderSkeleton(int proofFieldIndex, byte[][] fields, byte[] encoded)
    {
        ProofFieldIndex = proofFieldIndex;
        Fields = fields;
        Encoded = encoded;
        Hash = LeanCommitment.SkeletonHash(encoded);
    }

    public int ProofFieldIndex { get; }
    public byte[][] Fields { get; }
    public byte[] Encoded { get; }
    public ValueHash256 Hash { get; }

    /// <summary>Splits a proof-bearing header into its skeleton.</summary>
    public static LeanHeaderSkeleton FromHeader(BlockHeader header, IHeaderDecoder decoder)
    {
        if (header.RecursiveStark is null) throw new ArgumentException("Header has no recursive STARK");
        byte[] encoded = decoder.EncodeAsBytes(header);
        RlpReader reader = new(encoded);
        int end = reader.ReadSequenceLength() + reader.Position;
        int count = reader.PeekNumberOfItemsRemaining(end, LeanProtocol.MaxHeaderFields + 1);
        if (count > LeanProtocol.MaxHeaderFields) throw new ArgumentException("Header has too many fields");
        byte[][] fields = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int length = reader.PeekNextRlpLength();
            fields[i] = reader.Read(length).ToArray();
        }
        // recursive_stark is the final field of an EIP-8288 header.
        int proofIndex = count - 1;
        fields[proofIndex] = [];
        return Create(proofIndex, fields);
    }

    private static LeanHeaderSkeleton Create(int proofIndex, byte[][] fields)
    {
        byte[][] entries = new byte[fields.Length][];
        for (int i = 0; i < fields.Length; i++) entries[i] = LeanRlp.EncodeBytes(fields[i]);
        byte[] encoded = LeanRlp.EncodeList(LeanRlp.EncodeUInt((ulong)proofIndex), LeanRlp.EncodeList(entries));
        return new(proofIndex, fields, encoded);
    }

    /// <summary>Decodes a skeleton, checking its encoding, field bounds, placeholder and per-entry canonical RLP.</summary>
    /// <exception cref="RlpException">The skeleton is malformed.</exception>
    public static LeanHeaderSkeleton Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length > LeanProtocol.MaxHeaderSkeletonBytes) throw new RlpException("Skeleton exceeds MAX_HEADER_SKELETON_BYTES");
        LeanRlpReader outer = new(encoded);
        LeanRlpReader reader = outer.ReadList();
        outer.End();
        ulong proofIndex = reader.ReadUInt64();
        LeanRlpReader entries = reader.ReadList();
        reader.End();
        int count = entries.CountRemaining(LeanProtocol.MaxHeaderFields);
        if (proofIndex >= (ulong)count) throw new RlpException("Proof field index is out of range");
        byte[][] fields = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = entries.ReadBytes();
            if ((ulong)i == proofIndex)
            {
                if (entry.Length != 0) throw new RlpException("Proof placeholder must be empty");
            }
            else
            {
                if (entry.Length == 0) throw new RlpException("Header field entry is empty");
                LeanRlpReader field = new(entry);
                field.ReadItem();
                field.End();
            }
            fields[i] = entry.ToArray();
        }
        entries.End();
        return new((int)proofIndex, fields, encoded.ToArray());
    }

    /// <summary>Concatenates the entries with <c>RLP([stark_proof, block_deps_hash])</c> at the placeholder under one list header.</summary>
    public byte[] Reconstruct(ReadOnlySpan<byte> starkProof, in ValueHash256 blockDepsHash)
    {
        byte[] proof = LeanRlp.EncodeList(LeanRlp.EncodeBytes(starkProof), LeanRlp.EncodeBytes(blockDepsHash.Bytes));
        int content = proof.Length;
        for (int i = 0; i < Fields.Length; i++) if (i != ProofFieldIndex) content += Fields[i].Length;
        byte[] header = new byte[LeanRlp.LengthOfList(content)];
        int position = LeanRlp.WriteListPrefix(header, content);
        for (int i = 0; i < Fields.Length; i++)
        {
            byte[] field = i == ProofFieldIndex ? proof : Fields[i];
            field.CopyTo(header, position);
            position += field.Length;
        }
        return header;
    }

    /// <summary>Checks the skeleton against its descriptor and the fork's header schema before any chunk is requested.</summary>
    /// <remarks>
    /// The schema check decodes the reconstruction with an empty proof and requires that re-encoding reproduces it exactly,
    /// which rejects wrong field counts, order, types, widths and double-encoded entries.
    /// </remarks>
    public bool TryValidate(LeanDescriptor descriptor, IHeaderDecoder decoder, ISpecProvider specProvider, out string? error)
    {
        error = null;
        if (descriptor.Kind != LeanProtocol.KindBlockProof || Hash != descriptor.SkeletonHash)
        {
            error = "skeleton hash mismatch";
            return false;
        }
        if (ProofFieldIndex != Fields.Length - 1)
        {
            error = "proof field index is not the recursive_stark position";
            return false;
        }
        byte[] reconstructed = Reconstruct([], descriptor.BlockDepsHash);
        BlockHeader? header;
        try
        {
            header = decoder.Decode(reconstructed);
        }
        catch (Exception exception) when (exception is RlpException or ArgumentException or InvalidOperationException
            or IndexOutOfRangeException or OverflowException)
        {
            error = $"skeleton does not decode as a header: {exception.Message}";
            return false;
        }
        if (header?.RecursiveStark is null || !specProvider.GetSpec(header).IsEip8288Enabled)
        {
            error = "skeleton is not an EIP-8288 header";
            return false;
        }
        if (!decoder.EncodeAsBytes(header).AsSpan().SequenceEqual(reconstructed))
        {
            error = "skeleton fields are not canonical for the header schema";
            return false;
        }
        if (header.Number != descriptor.BlockNumber || header.TxRoot?.ValueHash256 != descriptor.TransactionsRoot)
        {
            error = "skeleton disagrees with the descriptor context";
            return false;
        }
        return true;
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>EIP-8437 <c>descriptor = [byte_length, content_hash]</c>, committing to a whole wrapper; its object ID is the
/// <c>content_hash</c>.</summary>
/// <remarks>Descriptors carry no verifier identifier: the <c>eth/73</c> handshake's <c>forkid</c> already fixes
/// <c>AGGREGATED_VK</c>.</remarks>
public sealed class WrapperDescriptor
{
    /// <summary>The longest canonical descriptor: a three-byte length and a 32-byte hash.</summary>
    public const int MaxEncodedBytes = 38;

    private WrapperDescriptor(int byteLength, ValueHash256 contentHash, byte[] encoded)
    {
        ByteLength = byteLength;
        ContentHash = contentHash;
        Encoded = encoded;
    }

    public int ByteLength { get; }
    public ValueHash256 ContentHash { get; }

    /// <summary>The canonical RLP encoding, at most <see cref="MaxEncodedBytes"/>.</summary>
    public byte[] Encoded { get; }

    /// <summary>The wrapper's object ID, its <c>content_hash</c>.</summary>
    public ValueHash256 ObjectId => ContentHash;

    public static WrapperDescriptor Create(ReadOnlySpan<byte> wrapper)
    {
        if (wrapper.Length is 0 or > Eth73Protocol.MaxWrapperBytes) throw new ArgumentException("Wrapper length is out of bounds", nameof(wrapper));
        ValueHash256 contentHash = ValueKeccak.Compute(wrapper);
        return new(wrapper.Length, contentHash, Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)wrapper.Length), Eth73Rlp.EncodeBytes(contentHash.Bytes)));
    }

    public static WrapperDescriptor Decode(ReadOnlySpan<byte> encoded)
    {
        Eth73RlpReader outer = new(encoded);
        Eth73RlpReader fields = outer.ReadList();
        outer.End();
        ulong length = fields.ReadUInt64();
        if (length is 0 or > Eth73Protocol.MaxWrapperBytes) throw new RlpException("Wrapper length is out of bounds");
        ValueHash256 contentHash = fields.ReadHash();
        fields.End();
        return new((int)length, contentHash, encoded.ToArray());
    }

    public override string ToString() => $"wrapper {ContentHash.ToShortString()} ({ByteLength} bytes)";
}

/// <summary>The canonical hash-only EIP-8288 wrapper encoding, checked before envelope recovery and validation.</summary>
public static class WrapperBodies
{
    /// <summary>Checks the canonical wrapper encoding and returns its transaction hashes, in order.</summary>
    /// <param name="verificationBytes">The witness or proof bytes a verifier will decode.</param>
    public static ValueHash256[] ParseWrapper(ReadOnlySpan<byte> wrapper, out int verificationBytes)
    {
        verificationBytes = 0;
        if (wrapper.Length > Eth73Protocol.MaxWrapperBytes) throw new RlpException("Wrapper exceeds MAX_WRAPPER_BYTES");
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader fields = outer.ReadList();
        outer.End();
        Eth73RlpReader transactions = fields.ReadList();
        int count = transactions.CountRemaining(Eip8288Constants.MaxTxsPerWrapper);
        if (count == 0) throw new RlpException("Wrapper carries no transactions");
        ValueHash256[] hashes = new ValueHash256[count];
        for (int i = 0; i < count; i++)
        {
            hashes[i] = transactions.ReadHash();
            if (i > 0 && hashes[i - 1].CompareTo(hashes[i]) >= 0) throw new RlpException("Wrapper hashes are not strictly ascending");
        }
        transactions.End();
        ulong mode = fields.ReadUInt64();
        if (mode > 1) throw new RlpException("Unknown wrapper mode");
        Eth73RlpReader content = fields.ReadList();
        fields.End();
        Eth73RlpReader dependencies = content.ReadList();
        int dependencyLimit = mode == 0
            ? Eip8288Constants.MaxLeanSigDepsPerWrapper + Eip8288Constants.MaxLeanStarkDepsPerWrapper
            : Eip8288Constants.MaxDepsPerAggregate;
        int dependencyCount = dependencies.CountRemaining(dependencyLimit);
        if (dependencyCount == 0) throw new RlpException("Wrapper dependencies are empty");
        ReadOnlySpan<byte> previous = default;
        for (int i = 0; i < dependencyCount; i++)
        {
            ReadOnlySpan<byte> dependency = dependencies.ReadBytes();
            if (dependency.Length != Eip8288Constants.DependencyTripleLength || !dependency[..31].IsZero()
                || !Eip8288Dependencies.IsAcceptedScheme(dependency[31])) throw new RlpException("Invalid dependency");
            if (i > 0 && previous.SequenceCompareTo(dependency) >= 0) throw new RlpException("Dependencies are not canonical");
            previous = dependency;
        }
        if (mode == 0)
        {
            Eth73RlpReader proofs = content.ReadList();
            if (proofs.CountRemaining(dependencyLimit) != dependencyCount) throw new RlpException("Witness count mismatch");
            while (proofs.HasMore) verificationBytes += proofs.ReadBytes().Length;
        }
        else
        {
            ReadOnlySpan<byte> proof = content.ReadBytes();
            // EIP-8288 applies MAX_AGGREGATE_PROOF_BYTES to every aggregate proof before decoding or verification.
            if (proof.Length > Eip8288Constants.MaxAggregateProofBytes) throw new RlpException("Aggregate proof exceeds MAX_AGGREGATE_PROOF_BYTES");
            verificationBytes = proof.Length;
        }
        content.End();
        return hashes;
    }

    /// <inheritdoc cref="ParseWrapper(ReadOnlySpan{byte}, out int)"/>
    public static ValueHash256[] ParseWrapper(ReadOnlySpan<byte> wrapper) => ParseWrapper(wrapper, out _);

    /// <summary>Indices of <paramref name="hashes"/> missing from <paramref name="resolved"/>, ascending.</summary>
    internal static List<int> MissingIndices(ValueHash256[] hashes, IReadOnlyDictionary<ValueHash256, Transaction> resolved)
    {
        List<int> missing = [];
        for (int i = 0; i < hashes.Length; i++)
            if (!resolved.ContainsKey(hashes[i])) missing.Add(i);
        return missing;
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>An EIP-8437 NewProofWrapperHashes entry <c>[length, hash]</c>: a wrapper's encoded size and the Keccak-256 of
/// those exact bytes.</summary>
/// <remarks>Announcements carry no hints such as a verifier identifier: a receiver verifies under its own target fork
/// regardless, so one would authenticate nothing.</remarks>
public sealed class WrapperAnnouncement
{
    /// <summary>The longest canonical entry: a three-byte length and a 32-byte hash.</summary>
    public const int MaxEncodedBytes = 38;

    private WrapperAnnouncement(int length, ValueHash256 hash, byte[] encoded)
    {
        Length = length;
        Hash = hash;
        Encoded = encoded;
    }

    public int Length { get; }
    public ValueHash256 Hash { get; }

    /// <summary>The canonical RLP encoding, at most <see cref="MaxEncodedBytes"/>.</summary>
    public byte[] Encoded { get; }

    public static WrapperAnnouncement Create(ReadOnlySpan<byte> wrapper)
    {
        if (wrapper.Length is 0 or > Eth73Protocol.MaxWrapperBytes) throw new ArgumentException("Wrapper length is out of bounds", nameof(wrapper));
        ValueHash256 hash = ValueKeccak.Compute(wrapper);
        return new(wrapper.Length, hash, Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)wrapper.Length), Eth73Rlp.EncodeBytes(hash.Bytes)));
    }

    public static WrapperAnnouncement Decode(ReadOnlySpan<byte> encoded)
    {
        Eth73RlpReader outer = new(encoded);
        Eth73RlpReader fields = outer.ReadList();
        outer.End();
        ulong length = fields.ReadUInt64();
        if (length is 0 or > Eth73Protocol.MaxWrapperBytes) throw new RlpException("Wrapper length is out of bounds");
        ValueHash256 hash = fields.ReadHash();
        fields.End();
        return new((int)length, hash, encoded.ToArray());
    }

    public override string ToString() => $"wrapper {Hash.ToShortString()} ({Length} bytes)";
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
            ? Eip8288Constants.MaxLeanSigDepsPerDirectWrapper + Eip8288Constants.MaxLeanStarkDepsPerDirectWrapper
            : Eip8288Constants.MaxDepsPerAggregateWrapper;
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
}

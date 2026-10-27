// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>An EIP-8437 NewProofWrapperHashes entry <c>[mode, length, hash]</c>: a wrapper's EIP-8288 mode, its encoded size
/// and the Keccak-256 of those exact bytes.</summary>
/// <remarks>Mode and length are checked against the returned wrapper. Announcements carry no other hints, such as a verifier
/// identifier or dependency digest: identical wrappers share a hash, and a receiver verifies under its own target fork.</remarks>
public sealed class WrapperAnnouncement
{
    /// <summary>The longest canonical entry: a one-byte mode, a three-byte length and a 32-byte hash.</summary>
    public const int MaxEncodedBytes = 39;

    private WrapperAnnouncement(int mode, int length, ValueHash256 hash, byte[] encoded)
    {
        Mode = mode;
        Length = length;
        Hash = hash;
        Encoded = encoded;
    }

    /// <summary>EIP-8288's wrapper mode: 0 for direct dependencies, 1 for a recursive STARK.</summary>
    public int Mode { get; }
    public int Length { get; }
    public ValueHash256 Hash { get; }

    /// <summary>The canonical RLP encoding, at most <see cref="MaxEncodedBytes"/>.</summary>
    public byte[] Encoded { get; }

    /// <summary>Describes a wrapper, reading its mode from the encoding.</summary>
    /// <exception cref="RlpException">The wrapper does not carry a mode field where EIP-8288 places it.</exception>
    public static WrapperAnnouncement Create(ReadOnlySpan<byte> wrapper) => Create(wrapper, WrapperBodies.ReadMode(wrapper));

    /// <summary>Describes wrapper bytes under an explicit mode, as the EIP-8437 announcement vectors do for opaque bodies.</summary>
    public static WrapperAnnouncement Create(ReadOnlySpan<byte> wrapper, int mode)
    {
        if (wrapper.Length is 0 or > Eth73Protocol.MaxWrapperBytes) throw new ArgumentException("Wrapper length is out of bounds", nameof(wrapper));
        if (mode is not (0 or 1)) throw new ArgumentException("Wrapper mode must be 0 or 1", nameof(mode));
        ValueHash256 hash = ValueKeccak.Compute(wrapper);
        return new(mode, wrapper.Length, hash,
            Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)mode), Eth73Rlp.EncodeUInt((ulong)wrapper.Length), Eth73Rlp.EncodeBytes(hash.Bytes)));
    }

    public static WrapperAnnouncement Decode(ReadOnlySpan<byte> encoded)
    {
        Eth73RlpReader outer = new(encoded);
        Eth73RlpReader fields = outer.ReadList();
        outer.End();
        ulong mode = fields.ReadUInt64();
        if (mode > 1) throw new RlpException("Unknown wrapper mode");
        ulong length = fields.ReadUInt64();
        if (length is 0 or > Eth73Protocol.MaxWrapperBytes) throw new RlpException("Wrapper length is out of bounds");
        ValueHash256 hash = fields.ReadHash();
        fields.End();
        return new((int)mode, (int)length, hash, encoded.ToArray());
    }

    public override string ToString() => $"mode-{Mode} wrapper {Hash.ToShortString()} ({Length} bytes)";
}

/// <summary>What announcement suppression compares between wrappers: mode, proof bytes and verification work.</summary>
/// <param name="ProofBytes">The witness or proof bytes a verifier decodes.</param>
/// <param name="VerificationWork">Direct verifications priced at their EIP-8288 verification gas, or one recursive STARK
/// verification for mode 1.</param>
public readonly record struct WrapperShape(int Mode, int ProofBytes, long VerificationWork);

/// <summary>The canonical EIP-8288 wrapper encoding, checked on receipt before validation.</summary>
public static class WrapperBodies
{
    /// <summary>EIP-7594 cell proofs per blob in a blob network wrapper.</summary>
    private const int CellProofsPerBlob = 128;

    /// <summary>Checks the canonical wrapper encoding and returns its consensus transaction hashes, in order.</summary>
    /// <remarks>Checks the fork-independent rules: <c>MAX_WRAPPER_BYTES</c>, full transaction encodings in strictly ascending
    /// transaction-hash order, the dependency count shared by both modes, dependency ordering and the mode-0 witness count.
    /// Hash references are resolved before a returned wrapper is parsed, so an unresolved one is rejected.</remarks>
    public static ValueHash256[] ParseWrapper(ReadOnlySpan<byte> wrapper, out WrapperShape shape)
    {
        int verificationBytes = 0;
        long work = 0;
        if (wrapper.Length > Eth73Protocol.MaxWrapperBytes) throw new RlpException("Wrapper exceeds MAX_WRAPPER_BYTES");
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader fields = outer.ReadList();
        outer.End();
        Eth73RlpReader transactions = fields.ReadList();
        List<ValueHash256> hashes = [];
        while (transactions.HasMore)
        {
            if (transactions.IsListNext) throw new RlpException("Wrapper carries an unresolved transaction reference");
            ValueHash256 hash = TransactionHash(transactions.ReadBytes());
            if (hashes.Count > 0 && hashes[^1].CompareTo(hash) >= 0) throw new RlpException("Wrapper transactions are not in strictly ascending hash order");
            hashes.Add(hash);
        }
        if (hashes.Count == 0) throw new RlpException("Wrapper carries no transactions");
        ulong mode = fields.ReadUInt64();
        if (mode > 1) throw new RlpException("Unknown wrapper mode");
        Eth73RlpReader content = fields.ReadList();
        fields.End();
        Eth73RlpReader dependencies = content.ReadList();
        int dependencyCount = dependencies.CountRemaining(Eip8288Constants.MaxDepsPerWrapper);
        if (dependencyCount == 0) throw new RlpException("Wrapper dependencies are empty");
        ReadOnlySpan<byte> previous = default;
        for (int i = 0; i < dependencyCount; i++)
        {
            ReadOnlySpan<byte> dependency = dependencies.ReadBytes();
            // A reserved scheme is well-formed here; validation rejects it as a dependency-rule failure, without penalty.
            if (dependency.Length != Eip8288Constants.DependencyTripleLength || !dependency[..31].IsZero()
                || !Eip8288Dependencies.IsKnownScheme(dependency[31])) throw new RlpException("Invalid dependency");
            if (i > 0 && previous.SequenceCompareTo(dependency) >= 0) throw new RlpException("Dependencies are not canonical");
            previous = dependency;
            work += (long)(dependency[31] == Eip8288Constants.LeanStarkScheme
                ? Eip8288Constants.LeanStarkVerificationGas : Eip8288Constants.LeanSphincsVerificationGas);
        }
        if (mode == 0)
        {
            Eth73RlpReader proofs = content.ReadList();
            if (proofs.CountRemaining(Eip8288Constants.MaxDepsPerWrapper) != dependencyCount) throw new RlpException("Witness count mismatch");
            while (proofs.HasMore) verificationBytes += proofs.ReadBytes().Length;
        }
        else
        {
            ReadOnlySpan<byte> proof = content.ReadBytes();
            // The local pinned-prover bound applies to every aggregate proof before decoding or verification.
            if (proof.Length is 0 or > Eip8288Constants.MaxAggregateProofBytes) throw new RlpException("Aggregate proof is empty or exceeds the local proof size bound");
            verificationBytes = proof.Length;
            work = (long)Eip8288Constants.LeanStarkVerificationGas;
        }
        content.End();
        shape = new((int)mode, verificationBytes, work);
        return [.. hashes];
    }

    /// <summary>The consensus hash of a wrapped EIP-8141 transaction in its pooled network form: Keccak-256 of the EIP-2718
    /// envelope without any blob network wrapper, whose elided blobs, commitments and cell proofs are checked structurally.</summary>
    /// <remarks>Only the envelope structure and blob metadata binding are checked here; the transaction itself is decoded and
    /// validated with the wrapper.</remarks>
    public static ValueHash256 TransactionHash(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 2 || encoded[0] != (byte)TxType.FrameTx) throw new RlpException("Wrapper transaction is not an EIP-8141 envelope");
        Eth73RlpReader outer = new(encoded[1..]);
        Eth73RlpReader payload = outer.ReadList();
        outer.End();
        if (!payload.IsListNext)
        {
            if (LastField(payload).CountRemaining(int.MaxValue) != 0) throw new RlpException("Blob-carrying transaction lacks its network wrapper");
            return ValueKeccak.Compute(encoded);
        }
        ReadOnlySpan<byte> body = payload.ReadItem();
        if (payload.ReadUInt64() != 1) throw new RlpException("Unknown blob network wrapper version");
        if (payload.ReadList().HasMore) throw new RlpException("Wrapped blob data must be elided");
        Eth73RlpReader commitments = payload.ReadList();
        Eth73RlpReader proofs = payload.ReadList();
        payload.End();
        Eth73RlpReader bodyReader = new(body);
        Eth73RlpReader bodyFields = bodyReader.ReadList();
        Eth73RlpReader versionedHashes = LastField(bodyFields);
        int blobs = versionedHashes.CountRemaining(int.MaxValue);
        if (blobs == 0 || commitments.CountRemaining(blobs) != blobs) throw new RlpException("Blob commitment count mismatch");
        Span<byte> versioned = stackalloc byte[32];
        while (commitments.HasMore)
        {
            ReadOnlySpan<byte> commitment = commitments.ReadBytes();
            if (commitment.Length != 48) throw new RlpException("Invalid blob commitment");
            SHA256.HashData(commitment, versioned);
            versioned[0] = 0x01;
            if (!versionedHashes.ReadBytes().SequenceEqual(versioned)) throw new RlpException("Blob commitment does not match its versioned hash");
        }
        if (proofs.CountRemaining(blobs * CellProofsPerBlob) != blobs * CellProofsPerBlob) throw new RlpException("Cell proof count mismatch");
        while (proofs.HasMore)
            if (proofs.ReadBytes().Length != 48) throw new RlpException("Invalid cell proof");
        Span<byte> envelope = new byte[1 + body.Length];
        envelope[0] = (byte)TxType.FrameTx;
        body.CopyTo(envelope[1..]);
        return ValueKeccak.Compute(envelope);
    }

    /// <summary>The last field of a transaction payload, its blob versioned hashes.</summary>
    private static Eth73RlpReader LastField(Eth73RlpReader fields)
    {
        Eth73RlpReader last = default;
        bool any = false;
        while (fields.HasMore)
        {
            if (fields.IsListNext) last = fields.ReadList();
            else
            {
                fields.ReadItem();
                any = false;
                continue;
            }
            any = true;
        }
        return any ? last : throw new RlpException("Transaction payload does not end with its blob versioned hashes");
    }

    /// <inheritdoc cref="ParseWrapper(ReadOnlySpan{byte}, out WrapperShape)"/>
    public static ValueHash256[] ParseWrapper(ReadOnlySpan<byte> wrapper) => ParseWrapper(wrapper, out _);

    /// <summary>Reads the mode field, the second field of the wrapper list, without checking the rest of the encoding.</summary>
    /// <exception cref="RlpException">The encoding has no mode field of 0 or 1 in that position.</exception>
    public static int ReadMode(ReadOnlySpan<byte> wrapper)
    {
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader fields = outer.ReadList();
        fields.ReadItem();
        ulong mode = fields.ReadUInt64();
        return mode <= 1 ? (int)mode : throw new RlpException("Unknown wrapper mode");
    }

    /// <summary>The mode a returned wrapper declares, or -1 when it declares none, so it matches no announcement.</summary>
    public static int TryReadMode(ReadOnlySpan<byte> wrapper)
    {
        try { return ReadMode(wrapper); }
        catch (RlpException) { return -1; }
    }
}

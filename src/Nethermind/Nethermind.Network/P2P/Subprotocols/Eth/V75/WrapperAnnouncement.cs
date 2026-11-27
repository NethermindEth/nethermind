// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V75;

/// <summary>Announcement coverage hints: Keccak-256 of a wrapper's concatenated 96-byte <c>deps</c> entries and of its
/// concatenated, ascending consensus transaction hashes.</summary>
/// <remarks>Fork-independent: they hash encoded triples, not the circuit's dependency digest. Matching hints identify equal
/// coverage despite different proof bytes, but establish neither coverage nor validity until a wrapper is validated.</remarks>
public readonly record struct WrapperCoverage(ValueHash256 DepsHash, ValueHash256 TxsHash);

/// <summary>An EIP-8437 NewProofWrapperHashes entry <c>[mode, length, hash, txs-meta]</c>: a wrapper's EIP-8288 mode, its encoded
/// size, the Keccak-256 of those exact bytes and, for a mode-0 wrapper of at most
/// <see cref="Eth75Protocol.MaxInlineTransactions"/> transactions, their metadata.</summary>
/// <remarks>Mode, length and metadata are checked against the returned wrapper before its proof is verified.</remarks>
public sealed class WrapperAnnouncement
{
    internal WrapperAnnouncement(int mode, int length, ValueHash256 hash, WrapperCoverage? coverage, WrapperMetadata? metadata)
    {
        Mode = mode;
        Length = length;
        Hash = hash;
        Coverage = coverage;
        Metadata = metadata;
        Encoded = Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt((ulong)mode), Eth75Rlp.EncodeUInt((ulong)length), Eth75Rlp.EncodeBytes(hash.Bytes),
            metadata?.Encoded ?? Eth75Rlp.EncodeList());
    }

    /// <summary>EIP-8288's wrapper mode: 0 for direct dependencies, 1 for a recursive STARK.</summary>
    public int Mode { get; }
    public int Length { get; }
    public ValueHash256 Hash { get; }

    /// <summary>The coverage hints of a held wrapper; null for an announcement received without them.</summary>
    public WrapperCoverage? Coverage { get; }

    /// <summary>The inline transaction metadata; null when the announcement carries none.</summary>
    public WrapperMetadata? Metadata { get; }

    /// <summary>The canonical <c>eth/75</c> RLP encoding.</summary>
    public byte[] Encoded { get; }

    /// <summary>The canonical <c>eth/76</c> RLP encoding <c>[mode, length, hash, deps-hash, txs-hash, txs-meta]</c>.</summary>
    /// <exception cref="InvalidOperationException">The announcement has no coverage hints.</exception>
    public byte[] Encoded76 => _encoded76 ??= Coverage is { } coverage
        ? Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt((ulong)Mode), Eth75Rlp.EncodeUInt((ulong)Length), Eth75Rlp.EncodeBytes(Hash.Bytes),
            Eth75Rlp.EncodeBytes(coverage.DepsHash.Bytes), Eth75Rlp.EncodeBytes(coverage.TxsHash.Bytes), Metadata?.Encoded ?? Eth75Rlp.EncodeList())
        : throw new InvalidOperationException("Only an announcement with coverage hints has an eth/76 form");

    private byte[]? _encoded76;

    /// <summary>Describes a canonical wrapper, reading its mode, coverage hints and inline metadata from the encoding.</summary>
    /// <exception cref="RlpException">The wrapper is not a canonical EIP-8288 wrapper.</exception>
    public static WrapperAnnouncement Create(ReadOnlySpan<byte> wrapper)
    {
        WrapperBodies.ParseWrapper(wrapper, out WrapperShape shape, out WrapperCoverage coverage);
        return Create(wrapper, shape.Mode, coverage, WrapperMetadata.InlineFor(wrapper));
    }

    /// <summary>Describes wrapper bytes under an explicit mode, hints and metadata, as the EIP-8437 announcement vectors do for
    /// opaque bodies.</summary>
    public static WrapperAnnouncement Create(ReadOnlySpan<byte> wrapper, int mode, WrapperCoverage coverage, WrapperMetadata? metadata = null)
    {
        if (wrapper.Length is 0 or > Eth75Protocol.MaxWrapperBytes) throw new ArgumentException("Wrapper length is out of bounds", nameof(wrapper));
        if (mode is not (0 or 1)) throw new ArgumentException("Wrapper mode must be 0 or 1", nameof(mode));
        return new(mode, wrapper.Length, ValueKeccak.Compute(wrapper), coverage, metadata);
    }

    /// <summary>Decodes an entry, returning null for an unsupported mode once its encoding and length are checked.</summary>
    /// <param name="hash">The announced hash, which orders entries of every mode.</param>
    /// <exception cref="RlpException">The entry is malformed.</exception>
    public static WrapperAnnouncement? Decode(ReadOnlySpan<byte> encoded, out ValueHash256 hash)
    {
        Eth75RlpReader outer = new(encoded);
        Eth75RlpReader fields = outer.ReadList();
        outer.End();
        bool supported = fields.ReadUInt(out ulong mode) && mode == MempoolWrapper.ModeDirect;
        ulong length = fields.ReadUInt64();
        if (length is 0 or > Eth75Protocol.MaxWrapperBytes) throw new RlpException("Wrapper length is out of bounds");
        hash = fields.ReadHash();
        if (!fields.IsListNext) throw new RlpException("Transaction metadata must be a list");
        ReadOnlySpan<byte> metadata = fields.ReadItem();
        fields.End();
        if (!supported) return null;
        return new((int)mode, (int)length, hash, null,
            metadata.Length == 1 ? null : WrapperMetadata.Decode(metadata, (int)length, Eth75Protocol.MaxInlineTransactions));
    }

    /// <summary>Decodes an <c>eth/76</c> entry, returning null for a mode other than 0 or 1 once its encoding and length are
    /// checked.</summary>
    /// <remarks>Inline metadata is malformed on a mode-1 entry, and its hashes must be the preimage of <c>txs-hash</c>.</remarks>
    /// <param name="hash">The announced hash, which orders entries of every mode.</param>
    /// <exception cref="RlpException">The entry is malformed.</exception>
    public static WrapperAnnouncement? Decode76(ReadOnlySpan<byte> encoded, out ValueHash256 hash)
    {
        Eth75RlpReader outer = new(encoded);
        Eth75RlpReader fields = outer.ReadList();
        outer.End();
        bool supported = fields.ReadUInt(out ulong mode) && mode <= MempoolWrapper.ModeRecursive;
        ulong length = fields.ReadUInt64();
        if (length is 0 or > Eth75Protocol.MaxWrapperBytes) throw new RlpException("Wrapper length is out of bounds");
        hash = fields.ReadHash();
        WrapperCoverage coverage = new(fields.ReadHash(), fields.ReadHash());
        if (!fields.IsListNext) throw new RlpException("Transaction metadata must be a list");
        ReadOnlySpan<byte> encodedMetadata = fields.ReadItem();
        fields.End();
        if (!supported) return null;
        WrapperMetadata? metadata = null;
        if (encodedMetadata.Length != 1)
        {
            if (mode != MempoolWrapper.ModeDirect) throw new RlpException("A mode-1 announcement carries no inline metadata");
            metadata = WrapperMetadata.Decode(encodedMetadata, (int)length, Eth75Protocol.MaxInlineTransactions);
            if (metadata.TransactionsDigest != coverage.TxsHash) throw new RlpException("Transaction metadata does not match the announced txs-hash");
        }
        return new((int)mode, (int)length, hash, coverage, metadata);
    }

    public override string ToString() => $"mode-{Mode} wrapper {Hash.ToShortString()} ({Length} bytes)";
}

/// <summary>What announcement suppression compares between wrappers: mode, proof bytes and verification work.</summary>
/// <param name="ProofBytes">The witness or proof bytes a verifier decodes.</param>
/// <param name="VerificationWork">Direct verifications priced at their EIP-8288 verification gas, or one recursive STARK
/// verification for mode 1.</param>
/// <param name="LeanSphincsOnly">Whether every dependency uses <c>LEANSPHINCS_SCHEME</c>, the only scheme <c>eth/75</c> carries
/// (EIP-8437).</param>
public readonly record struct WrapperShape(int Mode, int ProofBytes, long VerificationWork, bool LeanSphincsOnly);

/// <summary>The canonical EIP-8288 wrapper encoding, checked on receipt before validation.</summary>
public static class WrapperBodies
{
    /// <summary>EIP-7594 cell proofs per blob in a blob network wrapper.</summary>
    private const int CellProofsPerBlob = 128;

    /// <summary>Checks the canonical wrapper encoding and returns its consensus transaction hashes, in order.</summary>
    /// <remarks>Checks the encoding rules: <c>MAX_WRAPPER_BYTES</c>, full transaction encodings in strictly ascending
    /// transaction-hash order, a nonempty, strictly ascending list of well-formed dependency triples and, in mode 0, one witness
    /// per dependency. Dependency rules, such as the mode-0 dependency limits, are left to validation, which discards a wrapper
    /// breaking them without penalty (EIP-8437). Hash references are resolved before a returned wrapper is parsed, so an
    /// unresolved one is rejected.</remarks>
    public static ValueHash256[] ParseWrapper(ReadOnlySpan<byte> wrapper, out WrapperShape shape, out WrapperCoverage coverage)
    {
        int verificationBytes = 0;
        long work = 0;
        bool leanSphincsOnly = true;
        if (wrapper.Length > Eth75Protocol.MaxWrapperBytes) throw new RlpException("Wrapper exceeds MAX_WRAPPER_BYTES");
        Eth75RlpReader outer = new(wrapper);
        Eth75RlpReader fields = outer.ReadList();
        outer.End();
        Eth75RlpReader transactions = fields.ReadList();
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
        Eth75RlpReader content = fields.ReadList();
        fields.End();
        Eth75RlpReader dependencies = content.ReadList();
        // Mode 1 has no dependency-count limit; MAX_WRAPPER_BYTES bounds either mode's list.
        int dependencyCount = dependencies.CountRemaining(Eip8288Constants.MaxWrapperDependencies);
        if (dependencyCount == 0) throw new RlpException("Wrapper carries no dependencies");
        byte[] dependencyEntries = new byte[dependencyCount * Eip8288Constants.DependencyTripleLength];
        for (int i = 0; i < dependencyCount; i++)
        {
            ReadOnlySpan<byte> dependency = dependencies.ReadBytes();
            // A reserved scheme is well-formed here; validation rejects it as a dependency-rule failure, without penalty.
            if (dependency.Length != Eip8288Constants.DependencyTripleLength || !dependency[..31].IsZero()) throw new RlpException("Invalid dependency");
            Span<byte> entry = dependencyEntries.AsSpan(i * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength);
            if (i > 0 && dependency.SequenceCompareTo(dependencyEntries.AsSpan((i - 1) * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength)) <= 0)
                throw new RlpException("Wrapper dependencies are not in strictly ascending order");
            dependency.CopyTo(entry);
            leanSphincsOnly &= dependency[31] == Eip8288Constants.LeanSphincsScheme;
            work += (long)(dependency[31] == Eip8288Constants.LeanStarkScheme
                ? Eip8288Constants.LeanStarkVerificationGas : Eip8288Constants.LeanSphincsVerificationGas);
        }
        if (mode == 0)
        {
            Eth75RlpReader proofs = content.ReadList();
            if (proofs.CountRemaining(dependencyCount) != dependencyCount) throw new RlpException("Wrapper does not carry one witness per dependency");
            while (proofs.HasMore) verificationBytes += proofs.ReadBytes().Length;
        }
        else
        {
            ReadOnlySpan<byte> proof = content.ReadBytes();
            if (proof.Length is 0 or > Eip8288Constants.MaxAggregateProofBytes) throw new RlpException("Aggregate proof is empty or exceeds the proof size bound");
            verificationBytes = proof.Length;
            work = (long)Eip8288Constants.LeanStarkVerificationGas;
        }
        content.End();
        shape = new((int)mode, verificationBytes, work, leanSphincsOnly);
        byte[] transactionHashes = new byte[hashes.Count * ValueHash256.MemorySize];
        for (int i = 0; i < hashes.Count; i++) hashes[i].Bytes.CopyTo(transactionHashes.AsSpan(i * ValueHash256.MemorySize));
        coverage = new(ValueKeccak.Compute(dependencyEntries), ValueKeccak.Compute(transactionHashes));
        return [.. hashes];
    }

    /// <summary>The consensus hash of a wrapped EIP-8141 transaction in its pooled network form: Keccak-256 of the EIP-2718
    /// envelope without any blob network wrapper, whose elided blobs, commitments and cell proofs are checked structurally.</summary>
    /// <remarks>Only the envelope structure and blob metadata binding are checked here; the transaction itself is decoded and
    /// validated with the wrapper.</remarks>
    public static ValueHash256 TransactionHash(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 2 || encoded[0] != (byte)TxType.FrameTx) throw new RlpException("Wrapper transaction is not an EIP-8141 envelope");
        Eth75RlpReader outer = new(encoded[1..]);
        Eth75RlpReader payload = outer.ReadList();
        outer.End();
        if (!payload.IsListNext)
        {
            if (LastField(payload).CountRemaining(int.MaxValue) != 0) throw new RlpException("Blob-carrying transaction lacks its network wrapper");
            return ValueKeccak.Compute(encoded);
        }
        ReadOnlySpan<byte> body = payload.ReadItem();
        if (payload.ReadUInt64() != 1) throw new RlpException("Unknown blob network wrapper version");
        if (payload.ReadList().HasMore) throw new RlpException("Wrapped blob data must be elided");
        Eth75RlpReader commitments = payload.ReadList();
        Eth75RlpReader proofs = payload.ReadList();
        payload.End();
        Eth75RlpReader bodyReader = new(body);
        Eth75RlpReader bodyFields = bodyReader.ReadList();
        Eth75RlpReader versionedHashes = LastField(bodyFields);
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
    private static Eth75RlpReader LastField(Eth75RlpReader fields)
    {
        Eth75RlpReader last = default;
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

    /// <inheritdoc cref="ParseWrapper(ReadOnlySpan{byte}, out WrapperShape, out WrapperCoverage)"/>
    public static ValueHash256[] ParseWrapper(ReadOnlySpan<byte> wrapper, out WrapperShape shape) => ParseWrapper(wrapper, out shape, out _);

    /// <inheritdoc cref="ParseWrapper(ReadOnlySpan{byte}, out WrapperShape, out WrapperCoverage)"/>
    public static ValueHash256[] ParseWrapper(ReadOnlySpan<byte> wrapper) => ParseWrapper(wrapper, out _, out _);

    /// <summary>Reads the mode field, the second field of the wrapper list, without checking the rest of the encoding.</summary>
    /// <exception cref="RlpException">The encoding has no mode field of 0 or 1 in that position.</exception>
    public static int ReadMode(ReadOnlySpan<byte> wrapper)
    {
        Eth75RlpReader outer = new(wrapper);
        Eth75RlpReader fields = outer.ReadList();
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

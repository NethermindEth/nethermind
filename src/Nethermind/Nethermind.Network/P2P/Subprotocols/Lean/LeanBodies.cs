// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Canonical body encodings of the three EIP-8437 object kinds, checked before kind validation.</summary>
public static class LeanBodies
{
    /// <summary>One kind-1 transaction entry: a full envelope (with its range in the body) or a hash reference.</summary>
    public readonly record struct WrapperEntry(ValueHash256 Hash, bool IsFull, int EnvelopeOffset, int EnvelopeLength);

    /// <summary>Parses <c>RLP([transactions, mode, [deps, proof_content]])</c> without decoding transactions.</summary>
    /// <exception cref="RlpException">The body is not a canonical kind-1 encoding.</exception>
    public static List<WrapperEntry> ParseWrapper(ReadOnlySpan<byte> body)
    {
        LeanRlpReader outer = new(body);
        LeanRlpReader wrapper = outer.ReadList();
        outer.End();
        LeanRlpReader transactions = wrapper.ReadList();
        int count = transactions.CountRemaining(LeanProtocol.MaxTxsPerObject);
        if (count == 0) throw new RlpException("Wrapper carries no transactions");
        List<WrapperEntry> entries = new(count);
        ValueHash256 previous = default;
        for (int i = 0; i < count; i++)
        {
            LeanRlpReader entry = transactions.ReadList();
            ulong tag = entry.ReadUInt64();
            ReadOnlySpan<byte> value = entry.ReadBytes();
            entry.End();
            WrapperEntry parsed = tag switch
            {
                0 when value.Length > 0 => new(ValueKeccak.Compute(value), true, OffsetOf(body, value), value.Length),
                1 when value.Length == ValueHash256.MemorySize => new(new ValueHash256(value), false, 0, 0),
                _ => throw new RlpException("Invalid wrapper transaction entry")
            };
            if (i > 0 && previous.Bytes.SequenceCompareTo(parsed.Hash.Bytes) >= 0)
                throw new RlpException("Wrapper transactions are not in strictly ascending hash order");
            previous = parsed.Hash;
            entries.Add(parsed);
        }
        transactions.End();
        ulong mode = wrapper.ReadUInt64();
        LeanRlpReader content = wrapper.ReadList();
        wrapper.End();
        LeanRlpReader deps = content.ReadList();
        while (deps.HasMore)
            if (deps.ReadBytes().Length != Eip8288Constants.DependencyTripleLength) throw new RlpException("Dependency is not 96 bytes");
        switch (mode)
        {
            case 0:
                {
                    LeanRlpReader proofs = content.ReadList();
                    while (proofs.HasMore) proofs.ReadBytes();
                    break;
                }
            case 1:
                content.ReadBytes();
                break;
            default:
                throw new RlpException("Unknown wrapper mode");
        }
        content.End();
        return entries;
    }

    /// <summary>Parses the kind-2 body <c>RLP([stark_proof])</c>.</summary>
    public static ReadOnlySpan<byte> ParseBlockProof(ReadOnlySpan<byte> body)
    {
        LeanRlpReader outer = new(body);
        LeanRlpReader list = outer.ReadList();
        outer.End();
        ReadOnlySpan<byte> proof = list.ReadBytes();
        list.End();
        return proof;
    }

    public static byte[] EncodeBlockProof(ReadOnlySpan<byte> starkProof) => LeanRlp.EncodeList(LeanRlp.EncodeBytes(starkProof));

    /// <summary>Checks the kind-3 body <c>RLP([transactions, [stark_proof, deps_hash]])</c>.</summary>
    public static void CheckInclusionList(ReadOnlySpan<byte> body)
    {
        LeanRlpReader outer = new(body);
        LeanRlpReader package = outer.ReadList();
        outer.End();
        LeanRlpReader transactions = package.ReadList();
        int count = transactions.CountRemaining(LeanProtocol.MaxTxsPerObject);
        for (int i = 0; i < count; i++)
            if (transactions.ReadBytes().Length == 0) throw new RlpException("Empty inclusion-list envelope");
        LeanRlpReader proof = package.ReadList();
        package.End();
        proof.ReadBytes();
        proof.ReadHash();
        proof.End();
    }

    /// <summary>Applies the canonical encoding check of <paramref name="kind"/>.</summary>
    public static void Check(byte kind, ReadOnlySpan<byte> body)
    {
        switch (kind)
        {
            case LeanProtocol.KindWrapper: ParseWrapper(body); break;
            case LeanProtocol.KindBlockProof: ParseBlockProof(body); break;
            case LeanProtocol.KindInclusionList: CheckInclusionList(body); break;
            default: throw new RlpException($"Unknown object kind {kind}");
        }
    }

    private static int OffsetOf(ReadOnlySpan<byte> body, ReadOnlySpan<byte> value) =>
        (int)(System.Runtime.CompilerServices.Unsafe.ByteOffset(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(body),
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(value)));
}

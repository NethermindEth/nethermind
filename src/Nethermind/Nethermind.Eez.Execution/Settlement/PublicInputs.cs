// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>The public inputs hash each proof system verifies for a batch, as the EEZ contract derives it.</summary>
public static class PublicInputs
{
    private const int HashSize = 32;

    /// <returns>One hash per proof system, in the order of the batch's proof systems.</returns>
    public static ValueHash256[] Compute(
        ReadOnlySpan<ValueHash256> entryHashes,
        ReadOnlySpan<ValueHash256> staticEntryHashes,
        ReadOnlySpan<ValueHash256> blobHashes,
        ReadOnlySpan<byte> callData,
        IReadOnlyList<RollupProofAssignment> assignments,
        int proofSystemCount,
        Address boundSender)
    {
        ValueHash256[] customDataHashes = new ValueHash256[assignments.Count];
        for (int i = 0; i < customDataHashes.Length; i++)
        {
            customDataHashes[i] = CustomDataHash(assignments[i].RollupId, assignments[i].CustomData);
        }

        ValueHash256 shared = SharedInput(entryHashes, staticEntryHashes, blobHashes, callData, customDataHashes, boundSender);
        ValueHash256[] hashes = new ValueHash256[proofSystemCount];
        for (int k = 0; k < hashes.Length; k++)
        {
            hashes[k] = ProofSystemHash(shared, assignments, (ulong)k);
        }

        return hashes;
    }

    /// <summary><c>keccak256(abi.encode(uint64 rollupId, bytes customData))</c>.</summary>
    public static ValueHash256 CustomDataHash(ulong rollupId, ReadOnlySpan<byte> customData)
    {
        byte[] buffer = new byte[3 * AbiWord.Size + AbiWord.PaddedLength(customData.Length)];
        Span<byte> span = buffer;
        AbiWord.Write(span[..32], rollupId);
        AbiWord.Write(span[32..64], 2UL * AbiWord.Size);
        AbiWord.Write(span[64..96], (ulong)customData.Length);
        customData.CopyTo(span[96..]);
        return ValueKeccak.Compute(buffer);
    }

    /// <summary>
    /// <c>keccak256(abi.encodePacked(abi.encode(entryHashes), abi.encode(staticEntryHashes), abi.encode(blobHashes),
    /// keccak256(callData), abi.encode(customDataHashes), boundSender))</c>.
    /// </summary>
    public static ValueHash256 SharedInput(
        ReadOnlySpan<ValueHash256> entryHashes,
        ReadOnlySpan<ValueHash256> staticEntryHashes,
        ReadOnlySpan<ValueHash256> blobHashes,
        ReadOnlySpan<byte> callData,
        ReadOnlySpan<ValueHash256> customDataHashes,
        Address boundSender)
    {
        int length = EncodedLength(entryHashes) + EncodedLength(staticEntryHashes) + EncodedLength(blobHashes)
            + HashSize + EncodedLength(customDataHashes) + Address.Size;
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> buffer = rented.AsSpan(0, length);
            int offset = Write(buffer, entryHashes);
            offset += Write(buffer[offset..], staticEntryHashes);
            offset += Write(buffer[offset..], blobHashes);
            ValueKeccak.Compute(callData).Bytes.CopyTo(buffer[offset..]);
            offset += HashSize;
            offset += Write(buffer[offset..], customDataHashes);
            boundSender.Bytes.CopyTo(buffer[offset..]);
            return ValueKeccak.Compute(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// <c>keccak256(shared || acc)</c>, where <c>acc</c> folds <c>abi.encode(acc, uint64 rollupId, bytes32 vkey)</c>
    /// over the rollups that require proof system <paramref name="proofSystemIndex"/>, in batch order.
    /// </summary>
    public static ValueHash256 ProofSystemHash(in ValueHash256 shared, IReadOnlyList<RollupProofAssignment> assignments, ulong proofSystemIndex)
    {
        Span<byte> step = stackalloc byte[3 * AbiWord.Size];
        ValueHash256 accumulator = default;
        foreach (RollupProofAssignment assignment in assignments)
        {
            int position = Array.BinarySearch(assignment.ProofSystemIndexes, proofSystemIndex);
            if (position < 0)
            {
                continue;
            }

            accumulator.Bytes.CopyTo(step);
            AbiWord.Write(step[32..64], assignment.RollupId);
            assignment.VerificationKeys[position].Bytes.CopyTo(step[64..]);
            accumulator = ValueKeccak.Compute(step);
        }

        Span<byte> result = stackalloc byte[2 * HashSize];
        shared.Bytes.CopyTo(result);
        accumulator.Bytes.CopyTo(result[HashSize..]);
        return ValueKeccak.Compute(result);
    }

    private static int EncodedLength(ReadOnlySpan<ValueHash256> hashes) => 2 * AbiWord.Size + hashes.Length * HashSize;

    /// <summary>Writes <c>abi.encode(bytes32[])</c>: the offset word, the length word, then the items.</summary>
    private static int Write(Span<byte> buffer, ReadOnlySpan<ValueHash256> hashes)
    {
        AbiWord.Write(buffer[..32], (ulong)AbiWord.Size);
        AbiWord.Write(buffer[32..64], (ulong)hashes.Length);
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i].Bytes.CopyTo(buffer[(64 + i * HashSize)..]);
        }

        return EncodedLength(hashes);
    }
}

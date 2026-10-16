// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>State helper for <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> keyed nonces: NONCE_MANAGER slot derivation and per-key nonce reads/consumption.</summary>
/// <remarks>All access goes through the supplied state, so NONCE_MANAGER slots enter the EIP-7928 block
/// access list — a missing slot makes a parallel validator reject a block sequential nodes accept.</remarks>
public static class KeyedNonceManager
{
    private const int SlotPreimageLength = 2 * 32;
    /// <summary>Batch width of <see cref="KeccakHash.ComputeHash64Bytes8Avx512"/>.</summary>
    private const int HashBatchSize = 8;
    private const int MinHashBatchSize = 2;

    [SkipLocalsInit]
    public static StorageCell StorageSlot(Address sender, in UInt256 nonceKey)
    {
        Span<byte> preimage = stackalloc byte[SlotPreimageLength];
        // Only the address's zero padding needs clearing; the rest of the preimage is fully overwritten.
        preimage[..(32 - Address.Size)].Clear();
        sender.Bytes.CopyTo(preimage[(32 - Address.Size)..32]);
        nonceKey.ToBigEndian(preimage[32..]);
        UInt256 index = new(ValueKeccak.Compute(preimage).Bytes, isBigEndian: true);
        return new StorageCell(Eip8250Constants.NonceManagerAddress, index);
    }

    public static ulong CurrentNonceSeq(IReadOnlyStateProvider state, Address sender, in UInt256 nonceKey) =>
        CurrentNonceSeq(state, StorageSlot(sender, nonceKey));

    private static ulong CurrentNonceSeq(IReadOnlyStateProvider state, in StorageCell slot)
    {
        state.Get(slot, out UInt256 stored);
        // Clamp so a crafted high-bit slot cannot false-match a valid nonce_seq < MAX_NONCE_SEQ.
        return stored > Eip8250Constants.MaxNonceSeq ? ulong.MaxValue : (ulong)stored;
    }

    public static bool IsFirstUse(IReadOnlyStateProvider state, Address sender, in UInt256 nonceKey) =>
        CurrentNonceSeq(state, sender, nonceKey) == 0;

    /// <summary>How many of <paramref name="nonceKeys"/> are unused, so consuming the set creates that many
    /// <c>NONCE_MANAGER</c> slots.</summary>
    /// <remarks>
    /// EIP-8250 prices each fresh slot as one storage-set state charge against the approving frame, so every
    /// path that grants payment approval owes the same count regardless of whether the approver carries code.
    /// The empty set writes no slot and counts zero.
    /// Counted slot by slot rather than inferred from the shared sequence: gas estimation reaches payment
    /// approval with nonce validation skipped, so there a partially used set is observable.
    /// </remarks>
    [SkipLocalsInit]
    public static int FirstUseCount(IReadOnlyStateProvider state, Address sender, ReadOnlySpan<UInt256> nonceKeys)
    {
        int firstUseCount = 0;
        if (Avx512F.IsSupported && nonceKeys.Length is >= MinHashBatchSize and <= Eip8250Constants.MaxNonceKeys)
        {
            Span<UInt256> indices = stackalloc UInt256[Eip8250Constants.MaxNonceKeys];
            StorageIndices(sender, nonceKeys, indices);
            for (int i = 0; i < nonceKeys.Length; i++)
            {
                StorageCell slot = new(Eip8250Constants.NonceManagerAddress, indices[i]);
                if (CurrentNonceSeq(state, slot) == 0) firstUseCount++;
            }

            return firstUseCount;
        }

        foreach (ref readonly UInt256 nonceKey in nonceKeys)
        {
            if (IsFirstUse(state, sender, in nonceKey)) firstUseCount++;
        }

        return firstUseCount;
    }

    [SkipLocalsInit]
    public static void ConsumeNonceSet(IWorldState state, Address sender, ReadOnlySpan<UInt256> nonceKeys, ulong nonceSeq)
    {
        if (nonceKeys.IsEmpty)
        {
            state.IncrementNonce(sender);
            return;
        }

        UInt256 nextSeq = nonceSeq == Eip8250Constants.MaxNonceSeq ? nonceSeq : (UInt256)nonceSeq + UInt256.One;

        if (Avx512F.IsSupported && nonceKeys.Length is >= MinHashBatchSize and <= Eip8250Constants.MaxNonceKeys)
        {
            Span<UInt256> indices = stackalloc UInt256[Eip8250Constants.MaxNonceKeys];
            StorageIndices(sender, nonceKeys, indices);
            for (int i = 0; i < nonceKeys.Length; i++)
            {
                state.Set(new StorageCell(Eip8250Constants.NonceManagerAddress, indices[i]), nextSeq);
            }
            return;
        }

        foreach (UInt256 nonceKey in nonceKeys)
        {
            state.Set(StorageSlot(sender, nonceKey), nextSeq);
        }
    }

    /// <summary>Whether <paramref name="nonceKeys"/> selects protocol-managed nonce domains rather than the sender's account nonce.</summary>
    /// <remarks>The empty set selects the account nonce, so only it keeps the account-nonce semantics every other transaction type has.</remarks>
    public static bool UsesKeyedDomain(ReadOnlySpan<UInt256> nonceKeys) => !nonceKeys.IsEmpty;

    /// <summary>Whether <paramref name="tx"/>'s replay protection lives in <c>NONCE_MANAGER</c> rather than the sender's account nonce.</summary>
    /// <remarks>Account-nonce filters and sender-bucket ordering are meaningless for such a transaction.</remarks>
    public static bool UsesKeyedNonce(Transaction tx) =>
        tx.NonceKeys is { } nonceKeys && UsesKeyedDomain(nonceKeys);

    /// <summary>Checks whether <paramref name="nonceKeys"/> is a well-formed <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> nonce-key set.</summary>
    /// <remarks>
    /// Well-formed means: length in <c>[0, <see cref="Eip8250Constants.MaxNonceKeys"/>]</c> and strictly increasing.
    /// </remarks>
    public static bool AreNonceKeysWellFormed(ReadOnlySpan<UInt256> nonceKeys)
    {
        if (nonceKeys.Length > Eip8250Constants.MaxNonceKeys)
        {
            return false;
        }

        for (int i = 1; i < nonceKeys.Length; i++)
        {
            if (nonceKeys[i] <= nonceKeys[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks whether <paramref name="nonceKeys"/>/<paramref name="nonceSeq"/> is a valid set to consume against <paramref name="sender"/>'s current state.</summary>
    /// <remarks>
    /// Requires <paramref name="nonceKeys"/> to be well-formed (see <see cref="AreNonceKeysWellFormed"/>). The empty set
    /// requires the account nonce to equal <paramref name="nonceSeq"/>, which must be below <see cref="Eip8250Constants.MaxNonceSeq"/>.
    /// Otherwise every key must be at <paramref name="nonceSeq"/> (per <see cref="CurrentNonceSeq"/>), or unused when
    /// <paramref name="nonceSeq"/> is <see cref="Eip8250Constants.MaxNonceSeq"/>. Safe to call on undecoded/untrusted input.
    /// </remarks>
    [SkipLocalsInit]
    public static bool IsNonceSetValid(IReadOnlyStateProvider state, Address sender, ReadOnlySpan<UInt256> nonceKeys, ulong nonceSeq)
    {
        if (!AreNonceKeysWellFormed(nonceKeys))
        {
            return false;
        }

        if (nonceKeys.IsEmpty)
        {
            return nonceSeq < Eip8250Constants.MaxNonceSeq && state.GetNonce(sender) == nonceSeq;
        }

        ulong expected = nonceSeq == Eip8250Constants.MaxNonceSeq ? 0 : nonceSeq;

        if (Avx512F.IsSupported && nonceKeys.Length >= MinHashBatchSize)
        {
            Span<UInt256> indices = stackalloc UInt256[Eip8250Constants.MaxNonceKeys];
            StorageIndices(sender, nonceKeys, indices);
            for (int i = 0; i < nonceKeys.Length; i++)
            {
                StorageCell slot = new(Eip8250Constants.NonceManagerAddress, indices[i]);
                if (CurrentNonceSeq(state, slot) != expected)
                {
                    return false;
                }
            }
            return true;
        }

        foreach (ref readonly UInt256 nonceKey in nonceKeys)
        {
            if (CurrentNonceSeq(state, sender, nonceKey) != expected)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Computes the NONCE_MANAGER storage index for each key in <paramref name="nonceKeys"/>, batching the Keccak hashes with AVX-512 where possible.</summary>
    [SkipLocalsInit]
    internal static void StorageIndices(Address sender, ReadOnlySpan<UInt256> nonceKeys, Span<UInt256> indices)
    {
        Debug.Assert(indices.Length >= nonceKeys.Length);

        int keyIndex = 0;
        if (Avx512F.IsSupported && nonceKeys.Length >= MinHashBatchSize)
        {
            Span<byte> preimages = stackalloc byte[SlotPreimageLength * HashBatchSize];
            Span<byte> hashes = stackalloc byte[Keccak.Size * HashBatchSize];
            preimages.Clear();

            for (int i = 0; i < HashBatchSize; i++)
            {
                Span<byte> senderBlock = preimages.Slice(i * SlotPreimageLength, 32);
                sender.Bytes.CopyTo(senderBlock[(32 - Address.Size)..]);
            }

            do
            {
                int count = Math.Min(HashBatchSize, nonceKeys.Length - keyIndex);
                for (int i = 0; i < count; i++)
                {
                    nonceKeys[keyIndex + i].ToBigEndian(preimages.Slice(i * SlotPreimageLength + 32, 32));
                }

                KeccakHash.ComputeHash64Bytes8Avx512(ref preimages[0], ref hashes[0]);
                for (int i = 0; i < count; i++)
                {
                    indices[keyIndex + i] = new UInt256(hashes.Slice(i * Keccak.Size, Keccak.Size), isBigEndian: true);
                }

                keyIndex += count;
            } while (keyIndex <= nonceKeys.Length - MinHashBatchSize);
        }

        for (; keyIndex < nonceKeys.Length; keyIndex++)
        {
            indices[keyIndex] = StorageSlot(sender, nonceKeys[keyIndex]).Index;
        }
    }
}

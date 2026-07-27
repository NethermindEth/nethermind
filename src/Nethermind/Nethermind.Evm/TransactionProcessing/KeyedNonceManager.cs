// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>State helper for <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> keyed nonces: NONCE_MANAGER slot derivation and per-key nonce reads/consumption.</summary>
public static class KeyedNonceManager
{
    private const int SlotPreimageLength = 2 * 32;

    public static StorageCell StorageSlot(Address sender, in UInt256 nonceKey)
    {
        Span<byte> preimage = stackalloc byte[SlotPreimageLength];
        preimage.Clear();
        sender.Bytes.CopyTo(preimage.Slice(32 - Address.Size, Address.Size));
        nonceKey.ToBigEndian(preimage.Slice(32));
        UInt256 index = new(ValueKeccak.Compute(preimage).Bytes, isBigEndian: true);
        return new StorageCell(Eip8250Constants.NonceManagerAddress, index);
    }

    public static ulong CurrentNonceSeq(IWorldState state, Address sender, in UInt256 nonceKey)
    {
        if (nonceKey.IsZero)
        {
            return state.GetNonce(sender);
        }

        UInt256 stored = new(state.Get(StorageSlot(sender, nonceKey)), isBigEndian: true);
        // Clamp so a crafted high-bit slot cannot false-match a valid nonce_seq < MAX_NONCE_SEQ.
        return stored > Eip8250Constants.MaxNonceSeq ? ulong.MaxValue : (ulong)stored;
    }

    public static bool IsFirstUse(IWorldState state, Address sender, in UInt256 nonceKey) =>
        !nonceKey.IsZero && CurrentNonceSeq(state, sender, nonceKey) == 0;

    public static void ConsumeNonceSet(IWorldState state, Address sender, ReadOnlySpan<UInt256> nonceKeys, ulong nonceSeq)
    {
        if (nonceKeys.Length == 1 && nonceKeys[0].IsZero)
        {
            state.IncrementNonce(sender);
            return;
        }

        Span<byte> buffer = stackalloc byte[32];
        ((UInt256)nonceSeq + UInt256.One).ToBigEndian(buffer);
        byte[] nextSeq = buffer.WithoutLeadingZeros().ToArray();
        foreach (UInt256 nonceKey in nonceKeys)
        {
            // EIP-8250 rejects key 0 in a non-[0] set; the decode-time validity check owns that, this guards the primitive.
            Debug.Assert(!nonceKey.IsZero, "key 0 must not appear in a non-[0] nonce_keys set");
            state.Set(StorageSlot(sender, nonceKey), nextSeq);
        }
    }

    /// <summary>Checks whether <paramref name="nonceKeys"/> is a well-formed <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> nonce-key set.</summary>
    /// <remarks>
    /// Well-formed means: length in <c>[1, <see cref="Eip8250Constants.MaxNonceKeys"/>]</c>; key 0 appears only as the
    /// singleton set <c>[0]</c>; and any multi-key set is strictly increasing, which also excludes a later 0.
    /// </remarks>
    public static bool AreNonceKeysWellFormed(ReadOnlySpan<UInt256> nonceKeys)
    {
        if (nonceKeys.Length < 1 || nonceKeys.Length > Eip8250Constants.MaxNonceKeys)
        {
            return false;
        }

        // Key 0 is valid only as the singleton [0].
        if (nonceKeys[0].IsZero)
        {
            return nonceKeys.Length == 1;
        }

        // Strictly increasing; as the first key is non-zero this also rejects any later 0.
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
    /// Requires all of: <paramref name="nonceKeys"/> is well-formed (see <see cref="AreNonceKeysWellFormed"/>),
    /// <paramref name="nonceSeq"/> is below <see cref="Eip8250Constants.MaxNonceSeq"/>, and every key in the set is
    /// currently at <paramref name="nonceSeq"/> (per <see cref="CurrentNonceSeq"/>). Safe to call on undecoded/untrusted input.
    /// </remarks>
    public static bool IsNonceSetValid(IWorldState state, Address sender, ReadOnlySpan<UInt256> nonceKeys, ulong nonceSeq)
    {
        if (!AreNonceKeysWellFormed(nonceKeys))
        {
            return false;
        }

        if (nonceSeq >= Eip8250Constants.MaxNonceSeq)
        {
            return false;
        }

        foreach (ref readonly UInt256 nonceKey in nonceKeys)
        {
            if (CurrentNonceSeq(state, sender, nonceKey) != nonceSeq)
            {
                return false;
            }
        }

        return true;
    }
}

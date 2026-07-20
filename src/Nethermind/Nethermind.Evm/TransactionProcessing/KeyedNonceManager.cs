// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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
        new UInt256(state.Get(StorageSlot(sender, nonceKey)), isBigEndian: true).IsZero;

    public static void ConsumeNonceSet(IWorldState state, Address sender, ReadOnlySpan<UInt256> nonceKeys, ulong nonceSeq, IReleaseSpec spec)
    {
        if (nonceKeys.Length == 1 && nonceKeys[0].IsZero)
        {
            state.IncrementNonce(sender);
            return;
        }

        byte[] nextSeq = ((UInt256)nonceSeq + UInt256.One).ToBigEndian().WithoutLeadingZeros().ToArray();
        foreach (UInt256 nonceKey in nonceKeys)
        {
            state.Set(StorageSlot(sender, nonceKey), nextSeq);
        }
    }
}

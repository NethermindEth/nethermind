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

/// <summary>
/// State helper for <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> keyed nonces:
/// derives <c>NONCE_MANAGER</c> storage slots and reads/consumes per-key nonce sequences.
/// </summary>
/// <remarks>
/// A <c>nonce_keys</c> set of exactly <c>[0]</c> aliases the legacy account nonce; any non-zero key
/// selects an independent nonce domain stored in the <c>NONCE_MANAGER</c> system contract. The
/// protocol never writes 0 to a keyed slot, so a zero read means the key has never been used.
/// </remarks>
public static class KeyedNonceManager
{
    /// <summary>Length of the slot pre-image: 32-byte left-padded sender followed by the 32-byte key.</summary>
    private const int SlotPreimageLength = 2 * 32;

    /// <summary>
    /// Derives the <c>NONCE_MANAGER</c> storage cell for the given <paramref name="sender"/> and
    /// <paramref name="nonceKey"/>, i.e. <c>keccak256(left_pad_32(sender) ‖ uint256_to_bytes32(nonceKey))</c>.
    /// </summary>
    public static StorageCell StorageSlot(Address sender, in UInt256 nonceKey)
    {
        Span<byte> preimage = stackalloc byte[SlotPreimageLength];
        preimage.Clear();
        sender.Bytes.CopyTo(preimage.Slice(32 - Address.Size, Address.Size));
        nonceKey.ToBigEndian(preimage.Slice(32));
        UInt256 index = new(ValueKeccak.Compute(preimage).Bytes, isBigEndian: true);
        return new StorageCell(Eip8250Constants.NonceManagerAddress, index);
    }

    /// <summary>
    /// Returns the current nonce sequence for <paramref name="sender"/> under <paramref name="nonceKey"/>.
    /// For key 0 this is the account nonce; otherwise it is the <c>NONCE_MANAGER</c> slot value, with an
    /// absent slot reading as 0.
    /// </summary>
    /// <remarks>
    /// A stored value exceeding <see cref="Eip8250Constants.MaxNonceSeq"/> is clamped to
    /// <see cref="ulong.MaxValue"/> so a crafted high-bit slot cannot false-match a valid
    /// <c>nonce_seq &lt; MAX_NONCE_SEQ</c>.
    /// </remarks>
    public static ulong CurrentNonceSeq(IWorldState state, Address sender, in UInt256 nonceKey)
    {
        if (nonceKey.IsZero)
        {
            return state.GetNonce(sender);
        }

        UInt256 stored = new(state.Get(StorageSlot(sender, nonceKey)), isBigEndian: true);
        return stored > Eip8250Constants.MaxNonceSeq ? ulong.MaxValue : (ulong)stored;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the raw <c>NONCE_MANAGER</c> slot for
    /// (<paramref name="sender"/>, <paramref name="nonceKey"/>) reads 0. Only meaningful for non-zero keys.
    /// </summary>
    public static bool IsFirstUse(IWorldState state, Address sender, in UInt256 nonceKey) =>
        new UInt256(state.Get(StorageSlot(sender, nonceKey)), isBigEndian: true).IsZero;

    /// <summary>
    /// Consumes the nonce set for <paramref name="sender"/>: increments the account nonce when
    /// <paramref name="nonceKeys"/> is exactly <c>[0]</c>, otherwise writes <c>nonceSeq + 1</c> to each
    /// key's <c>NONCE_MANAGER</c> slot.
    /// </summary>
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

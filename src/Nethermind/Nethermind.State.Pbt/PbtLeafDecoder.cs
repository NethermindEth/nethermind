// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>
/// Reads accounts and storage slots back out of the stem leaf blobs that hold them, which is the only
/// place this backend stores them.
/// </summary>
/// <remarks>
/// A blob is the complete 256-leaf subtree of its stem rather than a diff, so whichever tier of a
/// bundle walk produces one has answered the read: an absent leaf there means the value is not set, not
/// that the walk should continue below. Every tier — the in-memory layers and the persistence reader
/// alike — decodes through here so that they cannot drift apart.
/// </remarks>
internal static class PbtLeafDecoder
{
    /// <summary>The account whose EIP-8297 header leaves <paramref name="headerBlob"/> holds, or null when it holds neither of them.</summary>
    /// <remarks>
    /// An existing account with a zero nonce, a zero balance and no code encodes an all-zero
    /// <c>BASIC_DATA</c>, which the blob normalizes to absent — so <c>CODE_HASH</c> is what witnesses
    /// its existence, and only the absence of both means the account is gone. The storage root is not
    /// represented in this backend: slots live in the one tree, so it is always the empty-tree hash.
    /// </remarks>
    public static Account? DecodeAccount(scoped ReadOnlySpan<byte> headerBlob)
    {
        bool hasBasicData = StemLeafBlob.TryGetValue(headerBlob, PbtKeyDerivation.BasicDataLeafKey, out ReadOnlySpan<byte> basicData);
        bool hasCodeHash = StemLeafBlob.TryGetValue(headerBlob, PbtKeyDerivation.CodeHashLeafKey, out ReadOnlySpan<byte> codeHash);
        if (!hasBasicData && !hasCodeHash) return null;

        ulong nonce = 0;
        UInt256 balance = default;
        if (hasBasicData) PbtKeyDerivation.UnpackBasicData(basicData, out nonce, out balance);

        return new Account(nonce, balance, Keccak.EmptyTreeHash, hasCodeHash ? new Hash256(codeHash) : Keccak.OfAnEmptyString);
    }

    /// <summary>The stem and sub-index a slot's leaf lives at: the account header stem for the first 64 slots, a storage-zone stem for the rest.</summary>
    public static Stem SlotStem(Address address, in UInt256 slot, out byte subIndex)
    {
        if (!PbtKeyDerivation.IsHeaderSlot(slot))
        {
            return PbtKeyDerivation.StorageStem(address, slot, out subIndex);
        }

        subIndex = PbtKeyDerivation.HeaderSlotSubIndex(slot);
        return PbtKeyDerivation.AccountHeaderStem(address);
    }

    /// <summary>The slot value <paramref name="blob"/> holds at <paramref name="subIndex"/>; zero when the leaf is absent.</summary>
    public static EvmWord DecodeSlot(scoped ReadOnlySpan<byte> blob, byte subIndex) =>
        StemLeafBlob.TryGetValue(blob, subIndex, out ReadOnlySpan<byte> value) ? EvmWordSlot.FromStripped(value) : default;
}

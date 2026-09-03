// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Pbt;

/// <summary>
/// Derives the EIP-8297 tree location of one address's storage slots, memoizing the derivations that
/// a run of slots shares.
/// </summary>
/// <remarks>
/// Three results are cached: <c>blake3(address32)</c>, which prefixes every stem of the address; the
/// account header stem, constant per address; and the storage-zone stem, shared by the 256 slots of
/// one tree index (<c>slot &gt;&gt; 8</c>). Slots arriving in ascending order therefore cost one address
/// hash per address plus one suffix hash per 256-slot run, rather than two hashes per slot.
/// <para>
/// This is a mutable struct: hold it in a field or pass it by <c>ref</c>. Copying it by value silently
/// drops the memo, which costs performance but not correctness. It carries no thread safety — give
/// each writer its own.
/// </para>
/// </remarks>
public struct PbtSlotKeyDeriver(Address address)
{
    private ValueHash256 _addressPrefix;
    private bool _addressPrefixComputed;
    private Stem _headerStem;
    private bool _headerStemComputed;
    private UInt256 _lastTreeIndex;
    private Stem _lastStorageStem;
    private bool _hasStorageStem;

    /// <summary><c>blake3(address32)</c> — the flat account key, and the prefix every stem of this address is built on.</summary>
    public ValueHash256 AddressPrefix()
    {
        if (!_addressPrefixComputed)
        {
            _addressPrefix = PbtKeyDerivation.AddressKeyHash(address);
            _addressPrefixComputed = true;
        }

        return _addressPrefix;
    }

    /// <summary>The account header stem, which carries <c>BASIC_DATA</c>, <c>CODE_HASH</c>, the header slots and the header code chunks.</summary>
    public Stem HeaderStem()
    {
        if (!_headerStemComputed)
        {
            _headerStem = PbtKeyDerivation.AccountHeaderStem(AddressPrefix());
            _headerStemComputed = true;
        }

        return _headerStem;
    }

    /// <summary>Routes <paramref name="slot"/> to its stem and sub-index: the first 64 slots live in the account header, the rest in their own storage-zone stem.</summary>
    public Stem Derive(in UInt256 slot, out byte subIndex)
    {
        if (PbtKeyDerivation.IsHeaderSlot(slot))
        {
            subIndex = PbtKeyDerivation.HeaderSlotSubIndex(slot);
            return HeaderStem();
        }

        UInt256 treeIndex = slot >> 8;
        if (_hasStorageStem && treeIndex == _lastTreeIndex)
        {
            subIndex = (byte)(slot.u0 & 0xFF);
            return _lastStorageStem;
        }

        Stem stem = PbtKeyDerivation.StorageStem(address, AddressPrefix(), slot, out subIndex);
        (_lastStorageStem, _lastTreeIndex, _hasStorageStem) = (stem, treeIndex, true);
        return stem;
    }

    /// <summary>The full 32-byte tree key of <paramref name="slot"/>, which is also its flat storage key.</summary>
    public ValueHash256 TreeKey(in UInt256 slot) =>
        PbtKeyDerivation.TreeKey(Derive(slot, out byte subIndex), subIndex);
}

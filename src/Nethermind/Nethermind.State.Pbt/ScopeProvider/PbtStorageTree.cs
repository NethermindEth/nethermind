// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>
/// Per-address storage view over the scope's bundle. EIP-8297 accounts have no per-account
/// storage root — slots live directly in the unified tree — so <see cref="RootHash"/> is a constant.
/// </summary>
/// <remarks>
/// That constant is a placeholder, not evidence that the account holds no slot: an account's main storage
/// lives under stems derived from its address and the slot index, which cannot be enumerated within a read,
/// so <see cref="IsKnownEmpty"/> is always <c>false</c>. The world state consequently reads emptiness off
/// <see cref="RootHash"/> alone where it asks a question this backend cannot answer — most notably the
/// EIP-7610 creation-collision check behind <c>IWorldState.IsStorageEmpty</c>, which therefore lets creation
/// proceed at a legacy account that holds storage but no code and a zero nonce.
/// </remarks>
public sealed class PbtStorageTree(PbtWorldStateScope scope, Address address) : IWorldStateScopeProvider.IStorageTree
{
    public Hash256 RootHash => Keccak.EmptyTreeHash;

    public bool IsKnownEmpty => false;

    public byte[] Get(in UInt256 index)
    {
        EvmWord value = scope.Bundle.GetSlot(address, index);
        return EvmWordSlot.IsZero(value) ? StorageTree.ZeroBytes : EvmWordSlot.ToStrippedBytes(value);
    }

    public void HintSet(in UInt256 index, byte[]? value)
    {
    }
}

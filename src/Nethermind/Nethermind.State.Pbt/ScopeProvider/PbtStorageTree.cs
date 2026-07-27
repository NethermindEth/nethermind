// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides a per-address storage view over the scope's unified EIP-8297 tree.</summary>
/// <remarks>
/// EIP-8297 has no per-account storage root. Because storage stems cannot be enumerated, this view cannot
/// prove emptiness and <see cref="IsKnownEmpty"/> is always <c>false</c>. Consequently, the EIP-7610
/// creation-collision check cannot detect a legacy account that contains only storage.
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

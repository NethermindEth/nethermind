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
public sealed class PbtStorageTree(PbtWorldStateScope scope, Address address) : IWorldStateScopeProvider.IStorageTree
{
    public Hash256 RootHash => Keccak.EmptyTreeHash;

    public byte[] Get(in UInt256 index)
    {
        byte[]? value = scope.Bundle.GetSlot(address, index);
        return value is null || value.Length == 0 ? StorageTree.ZeroBytes : value;
    }

    public void HintSet(in UInt256 index, byte[]? value)
    {
    }
}

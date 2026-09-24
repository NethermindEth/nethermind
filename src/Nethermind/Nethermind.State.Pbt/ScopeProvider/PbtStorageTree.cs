// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides a per-address storage view over the scope's unified EIP-8297 tree.</summary>
public sealed class PbtStorageTree(
    PbtWorldStateScope scope,
    Address address) : IWorldStateScopeProvider.IStorageTree
{
    private readonly ValueHash256 _addressHash = PbtKeyDerivation.AddressKeyHash(address);

    public Hash256 RootHash => Keccak.EmptyTreeHash;

    /// <inheritdoc/>
    /// <remarks>
    /// PBT has no per-account storage root, and enumerating a contract's storage can exhaust memory, especially during
    /// parallel prewarming. Only an absent account is known empty, since removing an account removes its slots. Taken
    /// once, when the tree is created; the scope drops its trees after every write batch so none outlives the answer.
    /// </remarks>
    public bool IsKnownEmpty { get; } = scope.Get(address) is null;

    public void Get(in UInt256 index, out UInt256 value)
    {
        EvmWord word = scope.Bundle.GetSlot(address, _addressHash, index);
        value = EvmWordSlot.ToUInt256(in word);
    }

    public void HintSet(in UInt256 index) => scope.HintSet(address, in index);
}

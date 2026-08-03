// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides a per-address storage view over the scope's unified EIP-8297 tree.</summary>
/// <remarks>EIP-8297 has no per-account storage root; emptiness is established by the account's full-key storage prefix.</remarks>
public sealed class PbtStorageTree(
    PbtWorldStateScope scope,
    Address address) : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
{
    public Hash256 RootHash => Keccak.EmptyTreeHash;

    public bool IsKnownEmpty => !scope.Bundle.AnyLeaf(PbtStateKey.StoragePrefix(address));

    public byte[] Get(in UInt256 index)
    {
        EvmWord value = scope.Bundle.GetSlot(address, index);
        return EvmWordSlot.IsZero(value) ? StorageTree.ZeroBytes : EvmWordSlot.ToStrippedBytes(value);
    }

    public void HintSet(in UInt256 index, byte[]? value)
    {
        EvmWord word = EvmWordSlot.FromStripped(value ?? []);
        scope.Bundle.SetSlot(address, index, word);
        QueuePrewarm(index, multiProducer: false);
    }

    internal void QueuePrewarm(in UInt256 index, bool multiProducer) { }

    public bool WarmUpStorageTrie(UInt256 index, int sequenceId) => false;
}

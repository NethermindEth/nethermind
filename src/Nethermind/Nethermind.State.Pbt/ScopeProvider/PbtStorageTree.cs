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
/// <remarks>
/// EIP-8297 has no per-account storage root. Because storage stems cannot be enumerated, this view cannot
/// prove emptiness and <see cref="IsKnownEmpty"/> is always <c>false</c>. Consequently, the EIP-7610
/// creation-collision check cannot detect a legacy account that contains only storage.
/// </remarks>
public sealed class PbtStorageTree(
    PbtWorldStateScope scope,
    Address address,
    ITrieWarmer trieWarmer) : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
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
        EvmWord word = EvmWordSlot.FromStripped(value ?? []);
        scope.Bundle.SetSlot(address, index, word);
        QueuePrewarm(index, multiProducer: false);
    }

    internal void QueuePrewarm(in UInt256 index, bool multiProducer)
    {
        if (scope.IsDisposed) return;

        Stem stem = PbtKeyDerivation.StorageStem(address, index, out _);
        if (!scope.TryReservePrewarm(stem, out int sequenceId)) return;

        bool queued = multiProducer
            ? trieWarmer.PushSlotJobMpmc(this, index, sequenceId)
            : trieWarmer.PushSlotJob(this, index, sequenceId)
                || trieWarmer.PushSlotJobMpmc(this, index, sequenceId);
        if (!queued) scope.CancelPrewarm(stem);
    }

    public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
    {
        try
        {
            if (scope.IsDisposed || scope.HintSequenceId != sequenceId) return false;
            Stem stem = PbtKeyDerivation.StorageStem(address, index, out _);
            return scope.Bundle.WarmStemPath(scope.WriteLayout, scope.PartitionRoots, stem);
        }
        finally
        {
            scope.CompletePrewarm();
        }
    }
}

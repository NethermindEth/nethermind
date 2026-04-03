// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// Shared context between <see cref="StateDiffScopeProviderDecorator"/> and
/// <see cref="ParallelBlockValidationTransactionsExecutor"/>. Holds an in-memory
/// overlay of accumulated state diffs. Reads go through the overlay first.
/// The overlay is dumped to the inner write batch on <c>StartWriteBatch</c>.
/// </summary>
public class ParallelBlockExecutionContext
{
    public BlockHeader? LastBaseBlock { get; set; }

    // Overlay dictionaries — accumulated diffs from all processed txs
    public ConcurrentDictionary<AddressAsKey, Account?> AccountOverlay { get; } = new();
    public ConcurrentDictionary<StorageCell, byte[]> StorageOverlay { get; } = new();
    public ConcurrentDictionary<ValueHash256, byte[]> CodeOverlay { get; } = new();

    /// <summary>
    /// Merge a transaction's state diff into the overlay.
    /// </summary>
    public void MergeDiff(TransactionStateDiff diff)
    {
        foreach ((Address address, Account? account) in diff.AccountWrites)
            AccountOverlay[address] = account;

        foreach ((Address address, UInt256 index, byte[] value) in diff.StorageWrites)
            StorageOverlay[new StorageCell(address, index)] = value;

        foreach ((ValueHash256 codeHash, byte[] code) in diff.CodeWrites)
            CodeOverlay[codeHash] = code;
    }

    /// <summary>
    /// Dump the overlay into a write batch and clear it.
    /// Called by the decorator's <c>StartWriteBatch</c>.
    /// </summary>
    /// <summary>
    /// Dump the overlay into the inner scope and clear it.
    /// Accounts + code are written in a separate write batch first (committed on dispose)
    /// so that flat DB's <c>CreateStorageWriteBatch</c> can verify account existence.
    /// Storage is written into the provided <paramref name="writeBatch"/>.
    /// </summary>
    public void DumpAndClear(
        Evm.State.IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch,
        Evm.State.IWorldStateScopeProvider.IScope scope)
    {
        if (AccountOverlay.Count == 0 && StorageOverlay.Count == 0 && CodeOverlay.Count == 0)
            return;

        // Pass 1: accounts + code in a separate batch (committed on dispose)
        // so they're visible when CreateStorageWriteBatch verifies in flat DB
        using (Evm.State.IWorldStateScopeProvider.IWorldStateWriteBatch accountBatch = scope.StartWriteBatch(AccountOverlay.Count))
        {
            foreach (KeyValuePair<AddressAsKey, Account?> kv in AccountOverlay)
                accountBatch.Set(kv.Key, kv.Value);

            if (CodeOverlay.Count > 0)
            {
                using Evm.State.IWorldStateScopeProvider.ICodeSetter codeSetter = scope.CodeDb.BeginCodeWrite();
                foreach (KeyValuePair<ValueHash256, byte[]> kv in CodeOverlay)
                    codeSetter.Set(kv.Key, kv.Value);
            }
        }

        // Pass 2: storage in the main write batch (accounts now committed)
        if (StorageOverlay.Count > 0)
        {
            Dictionary<Address, List<(UInt256 Index, byte[] Value)>> storageByAddress = [];
            foreach (KeyValuePair<StorageCell, byte[]> kv in StorageOverlay)
            {
                StorageCell cell = kv.Key;
                if (!storageByAddress.TryGetValue(cell.Address, out List<(UInt256, byte[])>? list))
                {
                    list = [];
                    storageByAddress[cell.Address] = list;
                }
                list.Add((cell.Index, kv.Value));
            }

            foreach (KeyValuePair<Address, List<(UInt256 Index, byte[] Value)>> kv in storageByAddress)
            {
                using Evm.State.IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                    writeBatch.CreateStorageWriteBatch(kv.Key, kv.Value.Count);
                foreach ((UInt256 index, byte[] value) in kv.Value)
                    storageBatch.Set(index, value);
            }
        }

        AccountOverlay.Clear();
        StorageOverlay.Clear();
        CodeOverlay.Clear();
    }
}

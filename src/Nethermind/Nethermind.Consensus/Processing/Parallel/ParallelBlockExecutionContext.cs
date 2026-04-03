// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// Shared context between <see cref="StateDiffScopeProviderDecorator"/> and
/// <see cref="ParallelBlockValidationTransactionsExecutor"/>. The decorator feeds
/// it with scope information; the executor reads and injects diffs through it.
/// </summary>
public class ParallelBlockExecutionContext
{
    private IWorldStateScopeProvider.IScope? _activeScope;

    /// <summary>
    /// The base block header captured from the last <see cref="IWorldStateScopeProvider.BeginScope"/> call.
    /// Used by parallel workers to open scopes at the same base state.
    /// </summary>
    public BlockHeader? LastBaseBlock { get; set; }

    /// <summary>
    /// Set by the decorator when a scope is opened.
    /// </summary>
    public void SetActiveScope(IWorldStateScopeProvider.IScope scope) => _activeScope = scope;

    /// <summary>
    /// Inject a state diff directly into the active scope's trie via a write batch.
    /// </summary>
    public void InjectDiff(TransactionStateDiff diff)
    {
        if (_activeScope is null)
            throw new InvalidOperationException("Cannot inject diff without an active scope");

        using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = _activeScope.StartWriteBatch(diff.AccountWrites.Count);

        foreach ((Address address, Account? account) in diff.AccountWrites)
            writeBatch.Set(address, account);

        // Group storage writes by address
        Dictionary<Address, List<(UInt256 Index, byte[] Value)>>? storageByAddress = null;
        foreach ((Address address, UInt256 index, byte[] value) in diff.StorageWrites)
        {
            storageByAddress ??= [];
            if (!storageByAddress.TryGetValue(address, out List<(UInt256, byte[])>? list))
            {
                list = [];
                storageByAddress[address] = list;
            }
            list.Add((index, value));
        }

        if (storageByAddress is not null)
        {
            foreach (KeyValuePair<Address, List<(UInt256 Index, byte[] Value)>> kv in storageByAddress)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                    writeBatch.CreateStorageWriteBatch(kv.Key, kv.Value.Count);
                foreach ((UInt256 index, byte[] value) in kv.Value)
                    storageBatch.Set(index, value);
            }
        }

        if (diff.CodeWrites.Count > 0)
        {
            using IWorldStateScopeProvider.ICodeSetter codeSetter = _activeScope.CodeDb.BeginCodeWrite();
            foreach ((ValueHash256 codeHash, byte[] code) in diff.CodeWrites)
                codeSetter.Set(codeHash, code);
        }
    }

    /// <summary>
    /// Get the current root hash of the active scope (after all injected diffs).
    /// Used to open re-execution scopes at the current accumulated state.
    /// </summary>
    public Hash256 GetCurrentRootHash()
    {
        if (_activeScope is null)
            throw new InvalidOperationException("No active scope");
        _activeScope.UpdateRootHash();
        return _activeScope.RootHash;
    }
}

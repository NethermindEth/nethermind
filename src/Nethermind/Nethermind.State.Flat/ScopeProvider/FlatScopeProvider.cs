// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatScopeProvider(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatDbManager flatDbManager,
    IFlatDbConfig configuration,
    ITrieWarmer trieWarmer,
    ResourcePool.Usage usage,
    ILogManager logManager,
    bool isReadOnly)
    : IWorldStateScopeProvider
{
    // Write paths (block processing) wrap the durable production codeDb directly and benefit
    // from the cross-block persisted-code hint cache. Read-only paths wrap a ReadOnlyDb temp
    // buffer (writes are transient) and must NOT populate the hint cache — see TrieStoreScopeProvider.
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);
    private readonly PreservedSparseTrie _preservedSparseTrie = new();
    private readonly SparseAuthoritativeTracker _sparseTracker = new();
    // Cross-block preservation of the warmed Patricia account tree (non-sparse path). Constructed
    // once and shared across consecutive scopes, mirroring _preservedSparseTrie. Null unless the
    // opt-in flag is set and we're a writable, non-sparse scope.
    private readonly PreservedPatriciaTrie? _preservedPatriciaTrie =
        configuration.PreservePatriciaTrie && !configuration.UseSparseRootComputation && !isReadOnly
            ? new PreservedPatriciaTrie()
            : null;

    public bool HasRoot(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId currentState = new(baseBlock);
        SnapshotBundle snapshotBundle = flatDbManager.GatherSnapshotBundle(currentState, usage: usage);

        return new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            _preservedSparseTrie,
            _sparseTracker,
            _preservedPatriciaTrie,
            logManager,
            isReadOnly: isReadOnly);
    }
}

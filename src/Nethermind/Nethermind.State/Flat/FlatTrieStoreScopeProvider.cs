// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public class FlatTrieStoreScopeProvider : IWorldStateScopeProvider
{
    private readonly IFlatDiffRepository _flatDiffRepository;
    private readonly ILogManager _logManager;
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb;
    private readonly bool _isReadOnly;

    public FlatTrieStoreScopeProvider(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IFlatDiffRepository flatDiffRepository,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _flatDiffRepository = flatDiffRepository;
        _logManager = logManager;
        _codeDb = new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(codeDb);
        _isReadOnly = isReadOnly;
    }

    public bool HasRoot(BlockHeader? baseBlock)
    {
        return _flatDiffRepository.HasStateForBlock(new StateId(baseBlock));
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId currentState = new StateId(baseBlock);
        SnapshotBundle snapshotBundle = _flatDiffRepository.GatherReaderAtBaseBlock(currentState);
        return new FlatScopeProviderScope(
            currentState,
            snapshotBundle,
            _codeDb,
            _flatDiffRepository,
            _logManager,
            _isReadOnly
        );
    }
}

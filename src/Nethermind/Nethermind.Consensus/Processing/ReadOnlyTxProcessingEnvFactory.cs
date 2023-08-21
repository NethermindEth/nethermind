// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingEnvFactory
{
    private readonly IReadOnlyDbProvider? _readOnlyDbProvider;
    private readonly IReadOnlyTrieStore? _readOnlyTrieStore;
    private readonly ReadOnlyVerkleStateStore? _readOnlyVerkleStore;
    private readonly IReadOnlyBlockTree? _readOnlyBlockTree;
    private readonly ISpecProvider? _specProvider;
    private readonly ILogManager? _logManager;
    private readonly StateType _stateType;

    public ReadOnlyTxProcessingEnvFactory(
        IDbProvider? dbProvider,
        IReadOnlyTrieStore? trieStore,
        IBlockTree? blockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
        : this(dbProvider?.AsReadOnly(false), trieStore, blockTree?.AsReadOnly(), specProvider, logManager)
    {
    }

    public ReadOnlyTxProcessingEnvFactory(
        IDbProvider? dbProvider,
        ReadOnlyVerkleStateStore? trieStore,
        IBlockTree? blockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
        : this(dbProvider?.AsReadOnly(false), trieStore, blockTree?.AsReadOnly(), specProvider, logManager)
    {
    }

    public ReadOnlyTxProcessingEnvFactory(
        IReadOnlyDbProvider? readOnlyDbProvider,
        IReadOnlyTrieStore? readOnlyTrieStore,
        IReadOnlyBlockTree? readOnlyBlockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        _readOnlyDbProvider = readOnlyDbProvider;
        _readOnlyTrieStore = readOnlyTrieStore;
        _readOnlyBlockTree = readOnlyBlockTree;
        _specProvider = specProvider;
        _logManager = logManager;
        _stateType = StateType.Merkle;
    }

    public ReadOnlyTxProcessingEnvFactory(
        IReadOnlyDbProvider? readOnlyDbProvider,
        ReadOnlyVerkleStateStore? readOnlyTrieStore,
        IReadOnlyBlockTree? readOnlyBlockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        _readOnlyDbProvider = readOnlyDbProvider;
        _readOnlyVerkleStore = readOnlyTrieStore;
        _readOnlyBlockTree = readOnlyBlockTree;
        _specProvider = specProvider;
        _logManager = logManager;
        _stateType = StateType.Verkle;
    }

    public ReadOnlyTxProcessingEnv Create() => _stateType switch
    {
        StateType.Merkle => new ReadOnlyTxProcessingEnv(_readOnlyDbProvider, _readOnlyTrieStore, _readOnlyBlockTree,
            _specProvider, _logManager),
        StateType.Verkle => new ReadOnlyTxProcessingEnv(_readOnlyDbProvider, _readOnlyVerkleStore, _readOnlyBlockTree,
            _specProvider, _logManager),
        _ => throw new ArgumentOutOfRangeException()
    };
}

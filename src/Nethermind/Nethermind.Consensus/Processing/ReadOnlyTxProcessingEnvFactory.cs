// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingEnvFactory
{
    private readonly IReadOnlyDbProvider? _readOnlyDbProvider;
    private readonly IReadOnlyTrieStore? _readOnlyTrieStore;
    private readonly IReadOnlyBlockTree? _readOnlyBlockTree;
    private readonly ISpecProvider? _specProvider;
    private readonly ILogManager? _logManager;

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
    }

    public ReadOnlyTxProcessingEnv Create() => new(_readOnlyDbProvider, _readOnlyTrieStore, _readOnlyBlockTree, _specProvider, _logManager);
}

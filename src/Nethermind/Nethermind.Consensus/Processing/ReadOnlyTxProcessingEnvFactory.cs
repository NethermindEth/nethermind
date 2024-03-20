// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingEnvFactory
{
    private readonly IReadOnlyDbProvider _readOnlyDbProvider;
    private readonly IStateFactory _stateFactory;
    private readonly IReadOnlyBlockTree? _readOnlyBlockTree;
    private readonly ISpecProvider? _specProvider;
    private readonly ILogManager? _logManager;

    public ReadOnlyTxProcessingEnvFactory(
        IReadOnlyDbProvider readOnlyDbProvider,
        IStateFactory stateFactory,
        IBlockTree? blockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
        : this(readOnlyDbProvider, stateFactory, blockTree?.AsReadOnly(), specProvider, logManager)
    {
    }

    public ReadOnlyTxProcessingEnvFactory(
        IReadOnlyDbProvider readOnlyDbProvider,
        IStateFactory stateFactory,
        IReadOnlyBlockTree? readOnlyBlockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        _readOnlyDbProvider = readOnlyDbProvider;
        _stateFactory = stateFactory;
        _readOnlyBlockTree = readOnlyBlockTree;
        _specProvider = specProvider;
        _logManager = logManager;
    }

    public ReadOnlyTxProcessingEnv Create() => new(_readOnlyDbProvider, _stateFactory, _readOnlyBlockTree, _specProvider, _logManager);
}

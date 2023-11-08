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
    private readonly IWorldStateFactory _worldStateFactory;
    private readonly IReadOnlyBlockTree? _readOnlyBlockTree;
    private readonly ISpecProvider? _specProvider;
    private readonly ILogManager? _logManager;

    public ReadOnlyTxProcessingEnvFactory(
        IWorldStateFactory worldStateFactory,
        IBlockTree? blockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
        : this(worldStateFactory, blockTree?.AsReadOnly(), specProvider, logManager)
    {
    }

    public ReadOnlyTxProcessingEnvFactory(
        IWorldStateFactory worldStateFactory,
        IReadOnlyBlockTree? readOnlyBlockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        _worldStateFactory = worldStateFactory;
        _readOnlyBlockTree = readOnlyBlockTree;
        _specProvider = specProvider;
        _logManager = logManager;
    }

    public ReadOnlyTxProcessingEnv Create() => new(_worldStateFactory, _readOnlyBlockTree, _specProvider, _logManager);
}

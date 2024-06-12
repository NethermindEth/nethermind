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
    private readonly IWorldStateManager _worldStateManager;
    private readonly IReadOnlyBlockTree? _readOnlyBlockTree;
    private readonly ISpecProvider? _specProvider;
    private readonly ILogManager? _logManager;

    public ReadOnlyTxProcessingEnvFactory(
        IWorldStateManager worldStateManager,
        IBlockTree? blockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
        : this(worldStateManager, blockTree?.AsReadOnly(), specProvider, logManager)
    {
    }

    public ReadOnlyTxProcessingEnvFactory(
        IWorldStateManager worldStateManager,
        IReadOnlyBlockTree? readOnlyBlockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        _worldStateManager = worldStateManager;
        _readOnlyBlockTree = readOnlyBlockTree;
        _specProvider = specProvider;
        _logManager = logManager;
    }

    public ReadOnlyTxProcessingEnv Create() => new(_worldStateManager, _readOnlyBlockTree, _specProvider, _logManager);
}

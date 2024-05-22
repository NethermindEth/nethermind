// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingEnvFactory(
    IWorldStateManager worldStateManager,
    IReadOnlyBlockTree? readOnlyBlockTree,
    ISpecProvider? specProvider,
    ILogManager? logManager,
    PreBlockCaches? preBlockCaches = null)
{
    public ReadOnlyTxProcessingEnvFactory(
        IWorldStateManager worldStateManager,
        IBlockTree? blockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager,
        PreBlockCaches? preBlockCaches = null)
        : this(worldStateManager, blockTree?.AsReadOnly(), specProvider, logManager, preBlockCaches)
    {
    }

    public ReadOnlyTxProcessingEnv Create() => new(worldStateManager, readOnlyBlockTree, specProvider, logManager, preBlockCaches);
}

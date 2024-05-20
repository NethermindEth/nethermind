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
    bool shareGlobalHashes = false)
{
    public ReadOnlyTxProcessingEnvFactory(
        IWorldStateManager worldStateManager,
        IBlockTree? blockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager,
        bool shareGlobalHashes = false)
        : this(worldStateManager, blockTree?.AsReadOnly(), specProvider, logManager, shareGlobalHashes)
    {
    }

    public ReadOnlyTxProcessingEnv Create() => new(worldStateManager, readOnlyBlockTree, specProvider, logManager, shareGlobalHashes);
}

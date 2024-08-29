// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingEnvBase
{
    public IScopedWorldStateManager WorldStateManager { get; protected set; }
    public IStateReader StateReader { get; protected set; }
    public IBlockTree BlockTree { get; protected set; }
    public IBlockhashProvider BlockhashProvider { get; protected set; }

    public ISpecProvider SpecProvider { get; }
    public ILogManager? LogManager { get; set; }

    protected ReadOnlyTxProcessingEnvBase(
        IWorldStateManager worldStateManager,
        IBlockTree readOnlyBlockTree,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(worldStateManager);
        WorldStateManager = worldStateManager.CreateResettableWorldStateManager();
        SpecProvider = specProvider;
        StateReader = worldStateManager.GlobalStateReader;
        BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
        BlockhashProvider = new BlockhashProvider(BlockTree, specProvider, logManager);
        LogManager = logManager;
    }
}

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
    public IStateReader StateReader { get; protected set; }
    protected IWorldState StateProvider { get; set; }
    public IBlockTree BlockTree { get; protected set; }
    public IBlockhashProvider BlockhashProvider { get; protected set; }

    public ISpecProvider SpecProvider { get; }
    protected ILogManager LogManager { get; }

    protected ReadOnlyTxProcessingEnvBase(
        IStateReader stateReader,
        IWorldState stateProvider,
        IBlockTree readOnlyBlockTree,
        ISpecProvider specProvider,
        ILogManager logManager
    )
    {
        SpecProvider = specProvider;
        StateReader = stateReader;
        StateProvider = stateProvider;
        BlockTree = readOnlyBlockTree;
        BlockhashProvider = new BlockhashProvider(BlockTree, specProvider, StateProvider, logManager);
        LogManager = logManager;
    }
}

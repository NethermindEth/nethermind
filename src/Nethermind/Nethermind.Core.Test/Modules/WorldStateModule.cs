// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Init;
using Nethermind.State;

namespace Nethermind.Core.Test.Modules;

public class WorldStateModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<PruningTrieStateFactory>()
            .AddSingleton<PruningTrieStateFactoryOutput>()

            .Map<IWorldStateManager, PruningTrieStateFactoryOutput>((o) => o.WorldStateManager)
            .Map<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader)
            .Map<INodeStorage, PruningTrieStateFactoryOutput>((m) => m.NodeStorage);
    }

    // Just a wrapper to easily extract the output of `PruningTrieStateFactory` which do the actual initializations.
    private class PruningTrieStateFactoryOutput
    {
        public IWorldStateManager WorldStateManager { get; }
        public INodeStorage NodeStorage { get; }

        public PruningTrieStateFactoryOutput(PruningTrieStateFactory factory)
        {
            (IWorldStateManager worldStateManager, INodeStorage mainNodeStorage, CompositePruningTrigger _) = factory.Build();
            WorldStateManager = worldStateManager;
            NodeStorage = mainNodeStorage;
        }
    }
}

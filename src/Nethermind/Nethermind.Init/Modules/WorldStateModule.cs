// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Init.Modules;

public class WorldStateModule(IInitConfig initConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<FlatStateActivationPolicy>()
            .AddSingleton<FlatWorldStateManager>()

            .Bind<IWorldStateManager, FlatWorldStateManager>()

            .Map<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader)

            // Resolving the boundary also runs the policy's validation, for containers that do not run the ValidateFlatState step.
            .AddSingleton<FlatStateBoundary>()
            .AddSingleton<IStateBoundary, FlatStateActivationPolicy, FlatStateBoundary>((_, boundary) => boundary)

            .Bind<IFullStateFinder, FlatFullStateFinder>()
            .Bind<ISnapTrieFactory, FlatSnapTrieFactory>()
            .Bind<ITreeSyncStore, FlatTreeSyncStore>()
            .Bind<IBalHealing, FlatBalHealing>()

            // Prevent multiple concurrent verify trie.
            .AddSingleton<IVerifyTrieStarter, VerifyTrieStarter>()

            .RegisterSingletonJsonRpcModule<IVerifyTrieAdminRpcModule, VerifyTrieAdminRpcModule>()

            // Registered unconditionally so `nethermind verify-trie` can always find it. Carrying
            // [StepCommand] keeps it out of a normal node start; it runs only when selected below or by name.
            // Backend-agnostic: VerifyTrie resolves to whichever backend is active.
            .AddStep(typeof(RunVerifyTrie))
        ;

        if (initConfig.DiagnosticMode == DiagnosticMode.VerifyTrie)
            builder.SelectStepTarget(typeof(RunVerifyTrie));
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Advances the native PBT state by one canonical block from its BAL, without executing transactions.</summary>
/// <remarks>
/// The committed snapshot is keyed by the child's header root, exactly as the mirror keys the snapshots main
/// processing produces, so the two producers are interchangeable and <see cref="PbtDbManager.AddSnapshot"/>
/// de-duplicates whichever lands second.
/// </remarks>
internal sealed class PbtBalReplay(
    ILifetimeScope rootLifetime,
    IPbtDbManager manager,
    IPbtResourcePool resourcePool,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ISpecProvider specProvider,
    IPbtConfig config,
    ILogManager logManager)
{
    public void Apply(BlockHeader parent, BlockHeader child, ReadOnlyBlockAccessList blockAccessList)
    {
        SingleScopeProvider provider = new(this, parent, child);
        using ILifetimeScope scope = rootLifetime.BeginLifetimeScope(builder =>
            builder.RegisterInstance(provider).As<IWorldStateScopeProvider>().ExternallyOwned());
        IWorldState state = scope.Resolve<IWorldState>();
        if (!state.TryBeginScopeAtTarget(child, out IDisposable? stateScope))
            throw new InvalidOperationException("The replay scope opens once, for its own child.");
        using IDisposable _ = stateScope;
        provider.Scope!.UseAuthoritativeRoot(child.StateRoot!);
        MigrationBalStateChanges.Apply(blockAccessList, state, specProvider.GetSpec(child));
        state.CommitTree(child.Number);
    }

    private PbtWorldStateScope Open(BlockHeader parent)
    {
        StateId stateId = new(parent);
        return new PbtWorldStateScope(stateId, parent, manager.GatherBundle(stateId, PbtResourcePool.Usage.ReadOnlyProcessingEnv),
            new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(codeDb, isPersistent: true), manager,
            NullPbtChildHeaderSource.Instance, resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv, isReadOnly: false,
            new NoopTrieWarmer(), config, logManager);
    }

    private sealed class SingleScopeProvider(PbtBalReplay owner, BlockHeader parent, BlockHeader child) : IWorldStateScopeProvider
    {
        public PbtWorldStateScope? Scope { get; private set; }

        public bool HasRoot(BlockHeader? baseBlock) => false;

        public bool HasStateForTargetBlock(BlockHeader targetBlock) => targetBlock.Hash == child.Hash && Scope is null;

        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            if (!HasStateForTargetBlock(targetBlock))
            {
                scope = null;
                return false;
            }

            scope = Scope = owner.Open(parent);
            return true;
        }

        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            scope = null;
            return false;
        }
    }
}

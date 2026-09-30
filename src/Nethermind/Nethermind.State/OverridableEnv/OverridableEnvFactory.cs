// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.State.OverridableEnv;

public class OverridableEnvFactory(IWorldStateManager worldStateManager, ILifetimeScope parentLifetimeScope, ISpecProvider specProvider, IPrefixStateSeedSource? prefixSeeds = null)
    : IOverridableEnvFactory, ITraceEnvFactory
{
    public IOverridableEnv Create() => Create(readOverlay: null);

    public IOverridableEnv CreateForTracing() => Create(prefixSeeds is { Enabled: true } ? new StateReadOverlaySlot() : null);

    private IOverridableEnv Create(StateReadOverlaySlot? readOverlay)
    {
        IOverridableWorldScope overridableScope = worldStateManager.CreateOverridableWorldScope();
        IWorldStateScopeProvider scopeProvider = readOverlay is null
            ? overridableScope.WorldState
            : new OverlaidScopeProvider(overridableScope.WorldState, readOverlay);

        // eth_simulateV1 and the tracers share these envs and bring their own recorder, so only add one
        // where a diff can actually be read.
        IReleaseSpec finalSpec = specProvider.GetFinalSpec();
        bool recordsTransactionDiffs = finalSpec.IsEip7906Enabled && finalSpec.BlockLevelAccessListsEnabled;
        ILifetimeScope childLifetimeScope = parentLifetimeScope.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(scopeProvider);
            if (recordsTransactionDiffs)
            {
                // At scope level so the tx processor and the code repository share one slice.
                builder.AddDecorator<IWorldState>(static (_, inner) => new TracedAccessWorldState(inner, parallel: false));
            }
            builder
                .AddDecorator<ICodeInfoRepository, OverridableCodeInfoRepository>()
                .AddScoped<IOverridableCodeInfoRepository, ICodeInfoRepository>((codeInfoRepo) =>
                    codeInfoRepo as OverridableCodeInfoRepository
                    ?? throw new InvalidOperationException($"{nameof(ICodeInfoRepository)} must be decorated by {nameof(OverridableCodeInfoRepository)}."));
        });

        OverridableSpecProvider overridableSpecProvider = new(specProvider);
        return new OverridableEnv(overridableScope, childLifetimeScope, specProvider, overridableSpecProvider, readOverlay);
    }

    private class OverridableEnv(
        IOverridableWorldScope overridableScope,
        ILifetimeScope childLifetimeScope,
        ISpecProvider specProvider,
        OverridableSpecProvider overridableSpecProvider,
        StateReadOverlaySlot? readOverlay
    ) : Module, IOverridableEnv, IDisposable
    {
        private IDisposable? _worldScopeCloser;
        private readonly IOverridableCodeInfoRepository _codeInfoRepository = childLifetimeScope.Resolve<IOverridableCodeInfoRepository>();
        private readonly IWorldState _worldState = childLifetimeScope.Resolve<IWorldState>();

        public bool TryBuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride, IReleaseSpec? specOverride, BlockOverride? blockOverride, [NotNullWhen(true)] out IDisposable? scope)
        {
            // Open the scope on the real base block first (its committed (number, root) state), then apply the block
            // override (e.g. eth_call blockOverride.number) on top.
            if (!TryOpen(specOverride, () => _worldState.TryBeginScope(header, out _worldScopeCloser)))
            {
                scope = null;
                return false;
            }

            try
            {
                if (header is not null)
                {
                    blockOverride?.ApplyOverrides(header);

                    // Commit the override on top of the base state, tagged at the (possibly overridden) block number,
                    // so downstream reads and the EVM block context resolve it there. A block override with no state
                    // override still commits the unchanged state at the overridden number.
                    if (stateOverride is not null || blockOverride is not null)
                    {
                        _worldState.ApplyStateOverrides(_codeInfoRepository, stateOverride, specProvider.GetSpec(header), header.Number);
                        header.StateRoot = _worldState.StateRoot;
                    }
                }

                scope = new Scope(this);
                return true;
            }
            catch
            {
                Reset();
                throw;
            }
        }

        public bool TryBuildAndOverrideAtTarget(BlockHeader targetBlock, Dictionary<Address, AccountOverride>? stateOverride, IReleaseSpec? specOverride, [NotNullWhen(true)] out IDisposable? scope)
        {
            if (!TryOpen(specOverride, () => _worldState.TryBeginScopeAtTarget(targetBlock, out _worldScopeCloser)))
            {
                scope = null;
                return false;
            }

            try
            {
                // Committed on top of the parent state at the target's height, where the target's own commit lands too.
                if (stateOverride is not null)
                {
                    _worldState.ApplyStateOverrides(_codeInfoRepository, stateOverride, specProvider.GetSpec(targetBlock), targetBlock.Number);
                }

                scope = new Scope(this);
                return true;
            }
            catch
            {
                Reset();
                throw;
            }
        }

        private bool TryOpen(IReleaseSpec? specOverride, Func<bool> beginScope)
        {
            if (_worldScopeCloser is not null) throw new InvalidOperationException("Previous overridable world scope was not closed");

            Reset();

            if (specOverride is not null)
                overridableSpecProvider.SetOverride(specOverride);

            if (beginScope()) return true;

            Reset();
            return false;
        }

        private class Scope(OverridableEnv env) : IDisposable
        {
            public void Dispose() => env.Reset();
        }

        private void Reset()
        {
            readOverlay?.Disarm();
            _codeInfoRepository.ResetOverrides();
            overridableSpecProvider.ResetOverride();

            _worldScopeCloser?.Dispose();
            _worldScopeCloser = null;
            overridableScope.ResetOverrides();
        }

        protected override void Load(ContainerBuilder builder)
        {
            builder
                .AddScoped<IWorldState>(_worldState)
                .AddScoped<IStateReader>(overridableScope.GlobalStateReader)
                .AddScoped<IOverridableEnv>(this)
                .AddScoped<ICodeInfoRepository>(_codeInfoRepository)
                .AddScoped<IOverridableCodeInfoRepository>(_codeInfoRepository)
                .AddScoped<ISpecProvider>(overridableSpecProvider);

            if (readOverlay is not null) builder.AddScoped<StateReadOverlaySlot>(readOverlay);
        }

        public void Dispose() =>
            // Note: This is the env's dispose, not the scope dispose.
            childLifetimeScope.Dispose();
    }
}

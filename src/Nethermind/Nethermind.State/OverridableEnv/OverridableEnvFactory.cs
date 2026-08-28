// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.State.OverridableEnv;

public class OverridableEnvFactory(IWorldStateManager worldStateManager, ILifetimeScope parentLifetimeScope, ISpecProvider specProvider) : IOverridableEnvFactory
{
    public IOverridableEnv Create()
    {
        IOverridableWorldScope overridableScope = worldStateManager.CreateOverridableWorldScope();
        ILifetimeScope childLifetimeScope = parentLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddSingleton<IWorldStateScopeProvider>(overridableScope.WorldState)
            .AddDecorator<ICodeInfoRepository, OverridableCodeInfoRepository>()
            .AddScoped<IOverridableCodeInfoRepository, ICodeInfoRepository>((codeInfoRepo) => (codeInfoRepo as OverridableCodeInfoRepository)!));

        OverridableSpecProvider overridableSpecProvider = new(specProvider);
        return new OverridableEnv(overridableScope, childLifetimeScope, specProvider, overridableSpecProvider);
    }

    private class OverridableEnv(
        IOverridableWorldScope overridableScope,
        ILifetimeScope childLifetimeScope,
        ISpecProvider specProvider,
        OverridableSpecProvider overridableSpecProvider
    ) : Module, IOverridableEnv, IDisposable
    {
        private IDisposable? _worldScopeCloser;
        private readonly IOverridableCodeInfoRepository _codeInfoRepository = childLifetimeScope.Resolve<IOverridableCodeInfoRepository>();
        private readonly IWorldState _worldState = childLifetimeScope.Resolve<IWorldState>();

        public IDisposable BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null, IReleaseSpec? specOverride = null, BlockOverride? blockOverride = null)
        {
            if (_worldScopeCloser is not null) throw new InvalidOperationException("Previous overridable world scope was not closed");

            Reset();

            if (specOverride is not null)
                overridableSpecProvider.SetOverride(specOverride);

            // Open the scope on the real base block first (its committed (number, root) state), then apply the block
            // override (e.g. eth_call blockOverride.number) on top.
            _worldScopeCloser = _worldState.BeginScope(header);

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

                return new Scope(this);
            }
            catch
            {
                Reset();
                throw;
            }
        }

        private class Scope(OverridableEnv env) : IDisposable
        {
            public void Dispose() => env.Reset();
        }

        private void Reset()
        {
            _codeInfoRepository.ResetOverrides();
            overridableSpecProvider.ResetOverride();

            _worldScopeCloser?.Dispose();
            _worldScopeCloser = null;
            overridableScope.ResetOverrides();
        }

        protected override void Load(ContainerBuilder builder) =>
            builder
                .AddScoped<IWorldState>(_worldState)
                .AddScoped<IStateReader>(overridableScope.GlobalStateReader)
                .AddScoped<IOverridableEnv>(this)
                .AddScoped<ICodeInfoRepository>(_codeInfoRepository)
                .AddScoped<IOverridableCodeInfoRepository>(_codeInfoRepository)
                .AddScoped<ISpecProvider>(overridableSpecProvider)
            ;

        public void Dispose() =>
            // Note: This is the env's dispose, not the scope dispose.
            childLifetimeScope.Dispose();
    }
}

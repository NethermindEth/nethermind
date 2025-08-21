// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;

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

        return new OverridableEnv(overridableScope, childLifetimeScope, specProvider);
    }

    private class OverridableEnv(
        IOverridableWorldScope overridableScope,
        ILifetimeScope childLifetimeScope,
        ISpecProvider specProvider
    ) : Module, IOverridableEnv, IDisposable
    {
        private IDisposable? _worldScopeCloser;
        private readonly IOverridableCodeInfoRepository _codeInfoRepository = childLifetimeScope.Resolve<IOverridableCodeInfoRepository>();
        private readonly IWorldState _worldState = childLifetimeScope.Resolve<IWorldState>();

        public IDisposable BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride)
        {
            if (_worldScopeCloser is not null) throw new InvalidOperationException("Previous overridable world scope was not closed");

            Reset();
            _worldScopeCloser = _worldState.BeginScope(header);
            IDisposable scope = new Scope(this);

            if (stateOverride is not null)
            {
                _worldState.ApplyStateOverrides(_codeInfoRepository, stateOverride, specProvider.GetSpec(header), header.Number);
                header.StateRoot = _worldState.StateRoot;
            }

            return scope;
        }

        private class Scope(OverridableEnv env) : IDisposable
        {
            public void Dispose()
            {
                env.Reset();
            }
        }

        private void Reset()
        {
            _codeInfoRepository.ResetOverrides();

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
            ;

        public void Dispose()
        {
            // Note: This is the env's dispose, not the scope dispose.
            childLifetimeScope.Dispose();
        }
    }
}

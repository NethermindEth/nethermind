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

        public bool TryBuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride, IReleaseSpec? specOverride, [NotNullWhen(true)] out IDisposable? closer)
        {
            if (_worldScopeCloser is not null) throw new InvalidOperationException("Previous overridable world scope was not closed");

            Reset();

            if (specOverride is not null)
                overridableSpecProvider.SetOverride(specOverride);

            if (!_worldState.TryBeginScope(header, out _worldScopeCloser))
            {
                Reset();
                closer = null;
                return false;
            }

            try
            {
                if (stateOverride is not null && header is not null)
                {
                    _worldState.ApplyStateOverrides(_codeInfoRepository, stateOverride, specProvider.GetSpec(header), header.Number);
                    header.StateRoot = _worldState.StateRoot;
                }

                closer = new Scope(this);
                return true;
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

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Evm.OverridableEnv;

public class OverridableEnvFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ISpecProvider specProvider) : IOverridableEnvFactory
{
    public IOverridableEnv Create()
    {
        IOverridableWorldScope overridableScope = worldStateManager.CreateOverridableWorldScope();
        IOverridableCodeInfoRepository codeInfoRepository = new OverridableCodeInfoRepository(codeInfoRepositoryFunc());

        return new OverridableEnv(overridableScope, codeInfoRepository, specProvider);
    }

    private class OverridableEnv(
        IOverridableWorldScope overridableScope,
        IOverridableCodeInfoRepository codeInfoRepository,
        ISpecProvider specProvider
    ) : Module, IOverridableEnv
    {
        private IDisposable? _worldScopeCloser;

        public IDisposable BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride)
        {
            if (_worldScopeCloser is not null) throw new InvalidOperationException("Previous overridable world scope was not closed");

            Reset();
            _worldScopeCloser = overridableScope.BeginScope(header);
            IDisposable scope = new Scope(this);

            if (stateOverride is not null)
            {
                overridableScope.WorldState.ApplyStateOverrides(codeInfoRepository, stateOverride, specProvider.GetSpec(header), header.Number);
                header.StateRoot = overridableScope.WorldState.StateRoot;
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
            overridableScope.WorldState.Reset();
            codeInfoRepository.ResetOverrides();

            _worldScopeCloser?.Dispose();
            _worldScopeCloser = null;
        }

        protected override void Load(ContainerBuilder builder) =>
            builder
                .AddScoped<IWorldState>(overridableScope.WorldState)
                .AddScoped<IStateReader>(overridableScope.GlobalStateReader)
                .AddScoped<IOverridableEnv>(this)
                .AddScoped<ICodeInfoRepository>(codeInfoRepository);
    }
}

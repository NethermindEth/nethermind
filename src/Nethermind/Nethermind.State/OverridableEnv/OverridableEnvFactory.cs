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

namespace Nethermind.State.OverridableEnv;

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
        public IDisposable Build(Hash256 stateRoot)
        {
            Reset();
            overridableScope.WorldState.StateRoot = stateRoot;
            return new Scope(this);
        }

        public IDisposable BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride)
        {
            IDisposable scope = Build(header.StateRoot ?? throw new ArgumentException($"Block {header.Hash} state root is null", nameof(header)));
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
            overridableScope.ResetOverrides();
            overridableScope.WorldState.StateRoot = Keccak.EmptyTreeHash;
        }

        protected override void Load(ContainerBuilder builder) =>
            builder
                .AddScopedAsImplementedInterfaces(overridableScope.WorldState)
                .AddScoped<IStateReader>(overridableScope.GlobalStateReader)
                .AddScoped<IOverridableEnv>(this)
                .AddScoped<ICodeInfoRepository>(codeInfoRepository);
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>Measures cached SHA256 precompile runs served from each cache tier, and admission into an empty cache.</summary>
/// <remarks>
/// Both tiers store <see cref="Result{TData}"/> values, so their size sets the copy on each hit, the per-block
/// dictionary node, and the surviving tier's slots. Results are per call. <see cref="Admit"/> also runs SHA256 on
/// every call, as each one misses.
/// </remarks>
[MemoryDiagnoser]
public class PrecompileCacheBenchmarks
{
    private const int Inputs = 1024;

    private IContainer _container = null!;
    private readonly List<ILifetimeScope> _processingScopes = [];
    private ReadOnlyMemory<byte>[] _inputs = null!;
    private IPrecompile _blockTier = null!;
    private IPrecompile _survivingTier = null!;
    private IPrecompile _admitting = null!;
    private PrecompileCaches _admittingCaches = null!;

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        _inputs = new ReadOnlyMemory<byte>[Inputs];
        for (int i = 0; i < Inputs; i++)
        {
            byte[] input = new byte[32];
            BitConverter.TryWriteBytes(input, i);
            _inputs[i] = input;
        }

        (_blockTier, _) = CreateCachedSha256(survivingMaxEntries: Inputs);
        RunAll(_blockTier);

        (_survivingTier, PrecompileCaches survivingCaches) = CreateCachedSha256(survivingMaxEntries: Inputs);
        RunAll(_survivingTier);
        survivingCaches.ClearBlockCache(publishMetrics: false);

        (_admitting, _admittingCaches) = CreateCachedSha256(survivingMaxEntries: 0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (ILifetimeScope scope in _processingScopes) scope.Dispose();
        _container?.Dispose();
    }

    [Benchmark(OperationsPerInvoke = Inputs)]
    public void BlockTierHit() => RunAll(_blockTier);

    [Benchmark(OperationsPerInvoke = Inputs)]
    public void SurvivingTierHit() => RunAll(_survivingTier);

    [Benchmark(OperationsPerInvoke = Inputs)]
    public void Admit()
    {
        _admittingCaches.ClearBlockCache(publishMetrics: false);
        RunAll(_admitting);
    }

    private void RunAll(IPrecompile precompile)
    {
        ReadOnlyMemory<byte>[] inputs = _inputs;
        for (int i = 0; i < inputs.Length; i++)
        {
            if (!precompile.Run(inputs[i], Osaka.Instance))
                throw new InvalidOperationException("SHA256 precompile failed.");
        }
    }

    /// <summary>Opens a main processing scope with its own precompile caches and returns its cached SHA256.</summary>
    private (IPrecompile Precompile, PrecompileCaches Caches) CreateCachedSha256(int survivingMaxEntries)
    {
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        IMainProcessingModule[] mainModules = _container.Resolve<IMainProcessingModule[]>();
        ILifetimeScope scope = _container.BeginLifetimeScope(builder => builder
            .AddSingleton(scopeProvider)
            .AddSingleton(TestPreBlockCachesConfig.Small with { SurvivingPrecompileCacheMaxEntries = survivingMaxEntries })
            .AddModule(mainModules));
        _processingScopes.Add(scope);

        if (scope.Resolve<ICodeInfoRepository>() is not PrecompileCachedCodeInfoRepository repository)
            throw new InvalidOperationException("Main processing modules did not register the precompile cache.");
        return (repository.GetPrecompile(Sha256Precompile.Address, Osaka.Instance)!, scope.Resolve<PrecompileCaches>());
    }
}

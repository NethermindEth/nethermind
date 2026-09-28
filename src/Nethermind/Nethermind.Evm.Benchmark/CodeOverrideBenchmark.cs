// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
public class CodeOverrideBenchmark
{
    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private IOverridableCodeInfoRepository _repository = null!;
    private CodeInfo _code = null!;

    [Params(32, 1024, 24576)]
    public int CodeLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        IOverridableEnv env = _container.Resolve<IOverridableEnvFactory>().Create();
        _scope = _container.BeginLifetimeScope(builder => builder.AddModule(env));
        _repository = _scope.Resolve<IOverridableCodeInfoRepository>();
        byte[] bytes = new byte[CodeLength];
        new System.Random(42).NextBytes(bytes);
        _code = new CodeInfo(bytes);
    }

    [Benchmark]
    public void SetCodeOverride() => _repository.SetCodeOverride(Prague.Instance, TestItem.AddressA, _code);

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _container.Dispose();
    }
}

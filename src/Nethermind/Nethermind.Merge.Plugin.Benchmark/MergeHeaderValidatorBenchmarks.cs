// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Benchmark;

[MemoryDiagnoser]
public class MergeHeaderValidatorBenchmarks
{
    private const ulong TerminalTotalDifficulty = 10;

    [Params(64)]
    public int Headers;

    private IContainer _container = null!;
    private IHeaderValidator _validator = null!;
    private BlockHeader[] _headers = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        TestSpecProvider specProvider = new(Paris.Instance) { TerminalTotalDifficulty = TerminalTotalDifficulty };
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton(new ChainSpec { Parameters = new ChainParameters { TerminalTotalDifficulty = TerminalTotalDifficulty } })
            .AddSingleton<IMergeConfig>(new MergeConfig { TerminalTotalDifficulty = TerminalTotalDifficulty.ToString() })
            .AddSingleton<IPoSSwitcher, PoSSwitcher>()
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<IHeaderValidator>(Substitute.For<IHeaderValidator>())
            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .Build();
        _validator = _container.Resolve<IHeaderValidator>();

        _headers = new BlockHeader[Headers + 1];
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(20_000_000)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithGasLimit(36_000_000)
            .WithGasUsed(18_000_000)
            .WithBaseFee(10_000_000_000)
            .WithTimestamp(1_700_000_000)
            .WithTotalDifficulty(TerminalTotalDifficulty)
            .TestObject;
        _headers[0] = parent;
        for (int i = 1; i <= Headers; i++)
        {
            BlockHeader header = Build.A.BlockHeader
                .WithParent(parent)
                .WithDifficulty(0)
                .WithNonce(0)
                .WithGasLimit(parent.GasLimit)
                .WithGasUsed(parent.GasLimit / 2)
                .WithBaseFee(BaseFeeCalculator.Calculate(parent, Paris.Instance))
                .WithTimestamp(parent.Timestamp + 12)
                .WithTotalDifficulty(TerminalTotalDifficulty)
                .TestObject;
            _headers[i] = header;
            parent = header;
        }

        for (int i = 1; i <= Headers; i++)
        {
            if (!_validator.Validate(_headers[i], _headers[i - 1], false, out string? error, false))
            {
                throw new InvalidOperationException($"Header {i} is invalid: {error}");
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _container.Dispose();

    [Benchmark]
    public int ValidatePostMergeHeaders()
    {
        int valid = 0;
        BlockHeader[] headers = _headers;
        for (int i = 1; i < headers.Length; i++)
        {
            if (_validator.Validate(headers[i], headers[i - 1], false, out _, false)) valid++;
        }

        return valid;
    }
}

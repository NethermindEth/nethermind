// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class PointEvaluationBenchmark : PrecompileBenchmarkBase
{
    [GlobalSetup]
    public async Task GlobalSetup() => await KzgPolynomialCommitments.InitializeAsync();

    protected override IEnumerable<IPrecompile> Precompiles => new[] { PointEvaluationPrecompile.Instance };
    protected override string InputsDirectory => "point_evaluation";
}

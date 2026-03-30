// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class KzgPointEvaluationBenchmark : PrecompileBenchmarkBase
{
    [GlobalSetup]
    public void GlobalSetup() => KzgPolynomialCommitments.Initialize();

    protected override IEnumerable<IPrecompile> Precompiles => new[] { KzgPointEvaluationPrecompile.Instance };
    protected override string InputsDirectory => "point_evaluation";
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    public class Bn256MulBenchmark : PrecompileBenchmarkBase
    {
        protected override IEnumerable<IPrecompile> Precompiles => new[]
        {
            Evm.Precompiles.Snarks.Shamatar.Bn256MulPrecompile.Instance
        };

        protected override string InputsDirectory => "bnmul";
    }
}

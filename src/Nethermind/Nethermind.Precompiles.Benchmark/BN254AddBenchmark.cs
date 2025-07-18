// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class BN254AddBenchmark : PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles => new[]
    {
        BN254AddPrecompile.Instance
    };

    protected override string InputsDirectory => "bnadd";
}

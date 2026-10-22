// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class Bls12381G1MsmBenchmark : PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles => [Bls12381G1MsmPrecompile.Instance];

    protected override string InputsDirectory => "blsg1msm";
}

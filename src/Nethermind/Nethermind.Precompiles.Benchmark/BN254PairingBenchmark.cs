// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class BN254PairingBenchmark : PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles => new[]
    {
        BN254PairingPrecompile.Instance
    };

    protected override string InputsDirectory => "bnpair";
}

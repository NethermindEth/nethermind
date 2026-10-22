// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class Bls12381PairingCheckBenchmark : PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles => [Bls12381PairingCheckPrecompile.Instance];

    protected override string InputsDirectory => "blspairingcheck";
}

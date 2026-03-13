// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class Bls12381Fp2ToG2Benchmark : PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles => [Bls12381Fp2ToG2Precompile.Instance];

    protected override string InputsDirectory => "blsmapfp2tog2";
}

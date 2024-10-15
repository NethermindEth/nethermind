// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;

namespace Nethermind.Precompiles.Benchmark;

public class BlsMapFp2ToG2Benchmark : PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles => new[]
    {
        MapFp2ToG2Precompile.Instance
    };

    protected override string InputsDirectory => "blsmapfp2tog2";
}

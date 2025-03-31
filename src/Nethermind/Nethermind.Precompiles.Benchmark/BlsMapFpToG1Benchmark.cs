// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;

namespace Nethermind.Precompiles.Benchmark;

public class BlsMapFpToG1Benchmark : PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles => new[]
    {
        MapFpToG1Precompile.Instance
    };

    protected override string InputsDirectory => "blsmapfptog1";
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class Secp256r1Benchmark: PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles => [Secp256r1Precompile.Instance];
    protected override string InputsDirectory => "secp256r1";
}

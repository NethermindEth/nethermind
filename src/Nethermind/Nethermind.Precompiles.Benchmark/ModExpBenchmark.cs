// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    public class ModExpBenchmark : PrecompileBenchmarkBase
    {
        protected override IEnumerable<IPrecompile> Precompiles => new[] { ModExpPrecompile.Instance };
        protected override string InputsDirectory => "modexp";

        [Benchmark]
        public (ReadOnlyMemory<byte>, bool) BigInt()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            return ModExpPrecompile.OldRun(Input.Bytes);
#pragma warning restore CS0618 // Type or member is obsolete
        }
    }
}

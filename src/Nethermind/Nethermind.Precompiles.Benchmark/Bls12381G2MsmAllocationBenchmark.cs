// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark;

/// <summary>
/// Measures per-call managed allocation of the BLS12-381 G2MSM precompile (<c>0x0f</c>) multi-point
/// decode path across vector sizes.
/// </summary>
/// <remarks>
/// Guards the scratch-buffer handling in <see cref="Bls12381G2MsmPrecompile"/>: the decode loop must
/// not allocate a per-call enumerator (it iterates an index range, not the destination list), so the
/// reported <c>Allocated</c> should stay flat as the iteration count grows rather than scaling with it.
/// The input repeats a single valid G2 point, so every vector exercises the full decode + native
/// Pippenger path with no infinity short-circuit.
/// </remarks>
[MemoryDiagnoser]
public class Bls12381G2MsmAllocationBenchmark
{
    // One valid 288-byte G2MSM item: a 256-byte G2 point in the correct subgroup followed by a 32-byte scalar.
    private const string ValidItem =
        "0000000000000000000000000000000003632695b09dbf86163909d2bb25995b36ad1d137cf252860fd4bb6c95749e19eb0c1383e9d2f93f2791cb0cf6c8ed9d000000000000000000000000000000001688a855609b0bbff4452d146396558ff18777f329fd4f76a96859dabfc6a6f6977c2496280dbe3b1f8923990c1d6407000000000000000000000000000000000c8567fee05d05af279adc67179468a29d7520b067dbb348ee315a99504f70a206538b81a457cce855f4851ad48b7e80000000000000000000000000000000001238dcdfa80ea46e1500026ea5feadb421de4409f4992ffbf5ae59fa67fd82f38452642a50261b849e74b4a33eed70cc973f40c12c92b703d7b7848ef8b4466d40823aad3943a312b57432b91ff68be1";

    [Params(2, 4, 8, 16)]
    public int Points { get; set; }

    private byte[] _input = [];

    [GlobalSetup]
    public void Setup()
    {
        byte[] item = Bytes.FromHexString(ValidItem);
        _input = new byte[item.Length * Points];
        for (int i = 0; i < Points; i++)
        {
            item.CopyTo(_input, i * item.Length);
        }
    }

    [Benchmark]
    public bool Msm() => Bls12381G2MsmPrecompile.Instance.Run(_input, Prague.Instance);
}

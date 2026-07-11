// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures the EVM memory rent -> grow -> access -> dispose cycle across realistic frame patterns.
/// The point is to compare, on the same workload, the cost of clearing memory: master zeroes the whole
/// buffer on dispose, whereas the clear-on-grow branch zeroes only the exposed gaps on growth. Runs the
/// same code on master and either branch (public + InternalsVisibleTo API only), so results are directly
/// comparable — see scripts/bench-evm-memory.sh.
/// </summary>
// In-process toolchain: avoids BenchmarkDotNet generating/building a separate project (which fails when
// the repo contains multiple copies of this project, e.g. git worktrees). Fine for relative comparison.
[MemoryDiagnoser]
[Config(typeof(Config))]
public class EvmMemoryBenchmarks
{
    private sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
    }

    // Frames per invocation: enough to reach steady state where the buffer pool is warm and dispose
    // dominates (this is where master pays its full-buffer clear and this branch pays little/nothing).
    private const int Frames = 64;

    private readonly byte[] _word = new byte[EvmPooledMemory.WordSize];

    [Params(4096, 65536, 262144)]
    public int Size;

    private byte[] _chunk = [];

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < _word.Length; i++) _word[i] = (byte)(i + 1);
        _chunk = new byte[Size];
        for (int i = 0; i < _chunk.Length; i++) _chunk[i] = (byte)i;
    }

    /// <summary>Typical Solidity frame: three contiguous scratch/free-pointer word stores, then dispose.</summary>
    [Benchmark(OperationsPerInvoke = Frames)]
    public void ScratchWrites()
    {
        for (int f = 0; f < Frames; f++)
        {
            EvmPooledMemory m = new();
            StoreWord(ref m, 0);
            StoreWord(ref m, 32);
            StoreWord(ref m, 64);
            m.Dispose();
        }
    }

    /// <summary>Build a buffer with contiguous word stores (ABI encoding / return construction), then dispose.
    /// The common growth pattern: every write fills the newly exposed word, so clear-on-grow clears nothing.</summary>
    [Benchmark(OperationsPerInvoke = Frames)]
    public void ContiguousStores()
    {
        for (int f = 0; f < Frames; f++)
        {
            EvmPooledMemory m = new();
            for (ulong off = 0; off < (ulong)Size; off += EvmPooledMemory.WordSize)
            {
                StoreWord(ref m, off);
            }
            m.Dispose();
        }
    }

    /// <summary>Single bulk copy filling the whole region (CALLDATACOPY/CODECOPY/RETURN build), then dispose.</summary>
    [Benchmark(OperationsPerInvoke = Frames)]
    public void BulkCopyWrite()
    {
        for (int f = 0; f < Frames; f++)
        {
            EvmPooledMemory m = new();
            m.TrySave(UInt256.Zero, _chunk);
            m.Dispose();
        }
    }

    /// <summary>Read (MLOAD/RETURN/KECCAK) a region that was never written — the whole span is a gap that
    /// must be zeroed on both master and this branch, so a near-tie is expected.</summary>
    [Benchmark(OperationsPerInvoke = Frames)]
    public void ReadGrow()
    {
        UInt256 length = (UInt256)Size;
        for (int f = 0; f < Frames; f++)
        {
            EvmPooledMemory m = new();
            m.TryLoadSpan(UInt256.Zero, in length, out _);
            m.Dispose();
        }
    }

    /// <summary>Scattered word stores leaving 224-byte gaps between them (worst case for this branch:
    /// each write pays a head-gap clear), then dispose.</summary>
    [Benchmark(OperationsPerInvoke = Frames)]
    public void SparseStores()
    {
        int count = Size / 256;
        for (int f = 0; f < Frames; f++)
        {
            EvmPooledMemory m = new();
            for (int i = 0; i < count; i++)
            {
                StoreWord(ref m, (ulong)(i * 256));
            }
            m.Dispose();
        }
    }

    private void StoreWord(ref EvmPooledMemory memory, ulong offset)
    {
        UInt256 location = offset;
        memory.CalculateMemoryCost(in location, EvmPooledMemory.WordSize, out _);
        memory.StoreWordAfterGas(in location, _word);
    }
}

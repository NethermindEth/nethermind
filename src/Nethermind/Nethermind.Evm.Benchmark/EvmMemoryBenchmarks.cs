// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures the EVM memory rent/grow/access/dispose cycle across representative frame patterns, to
/// compare the cost of memory-zeroing strategies on the same workload.
/// </summary>
// In-process toolchain: avoids building a separate project, which fails when the repo has multiple
// copies of it (e.g. git worktrees).
[MemoryDiagnoser]
[Config(typeof(Config))]
public class EvmMemoryBenchmarks
{
    private sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
    }

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

    // Typical Solidity scratch/free-pointer stores.
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

    // Contiguous word stores filling the region (ABI encoding / return construction).
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

    // Single bulk copy filling the whole region (CALLDATACOPY/CODECOPY/RETURN).
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

    // Read of a never-written region (MLOAD/RETURN/KECCAK): the whole span is a gap.
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

    // Scattered stores leaving 224-byte gaps between them.
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

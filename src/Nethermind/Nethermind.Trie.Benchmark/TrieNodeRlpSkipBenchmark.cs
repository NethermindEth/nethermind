// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Benchmark;

[MemoryDiagnoser]
public class TrieNodeRlpSkipBenchmark
{
    [Params(0, 2, 4, 8, 16)]
    public int PopulatedChildren { get; set; }

    private byte[] _branchRlp = Array.Empty<byte>();
    private CappedArray<byte> _cappedRlp;

    [GlobalSetup]
    public void Setup()
    {
        _branchRlp = BuildBranchRlp(PopulatedChildren);
        _cappedRlp = new CappedArray<byte>(_branchRlp);
    }

    // ── Baseline: original for-loop with SkipItem() ──

    [Benchmark(Baseline = true)]
    public int SeekChild15_Baseline()
    {
        ValueRlpStream rlpStream = new(_cappedRlp);
        rlpStream.Reset();
        rlpStream.SkipLength();
        for (int i = 0; i < 15; i++)
        {
            rlpStream.SkipItem();
        }
        return rlpStream.Position;
    }

    // ── Inline ulong empty-skip (copied from RefCountingTrieNodePool pattern) ──

    [Benchmark]
    public int SeekChild15_InlineUlong()
    {
        ValueRlpStream rlpStream = new(_cappedRlp);
        rlpStream.Reset();
        rlpStream.SkipLength();
        int i = 0;
        while (i < 15)
        {
            ulong val = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(rlpStream.Data), rlpStream.Position));
            int emptyCount = Math.Min(
                BitOperations.TrailingZeroCount(val ^ 0x8080808080808080UL) / 8,
                15 - i);
            if (emptyCount > 0)
            {
                rlpStream.Position += emptyCount;
                i += emptyCount;
                continue;
            }
            rlpStream.SkipItem();
            i++;
        }
        return rlpStream.Position;
    }

    // ── Full-branch fast path (direct offset) + inline ulong fallback ──

    [Benchmark]
    public int SeekChild15_FullBranch()
    {
        if (_branchRlp.Length == 532)
        {
            return 3 + 15 * 33;
        }

        ValueRlpStream rlpStream = new(_cappedRlp);
        rlpStream.Reset();
        rlpStream.SkipLength();
        int i = 0;
        while (i < 15)
        {
            ulong val = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(rlpStream.Data), rlpStream.Position));
            int emptyCount = Math.Min(
                BitOperations.TrailingZeroCount(val ^ 0x8080808080808080UL) / 8,
                15 - i);
            if (emptyCount > 0)
            {
                rlpStream.Position += emptyCount;
                i += emptyCount;
                continue;
            }
            rlpStream.SkipItem();
            i++;
        }
        return rlpStream.Position;
    }

    // ── Iterate all 16: baseline ──

    [Benchmark]
    public int IterateAll_Baseline()
    {
        ValueRlpStream rlpStream = new(_cappedRlp);
        rlpStream.Reset();
        rlpStream.SkipLength();
        int nullCount = 0;
        for (int i = 0; i < 16; i++)
        {
            int prefix = rlpStream.PeekByte();
            if (prefix == 128 || prefix == 0)
            {
                rlpStream.Position++;
                nullCount++;
            }
            else
            {
                rlpStream.SkipItem();
            }
        }
        return nullCount;
    }

    // ── Iterate all 16: inline ulong ──

    [Benchmark]
    public int IterateAll_InlineUlong()
    {
        ValueRlpStream rlpStream = new(_cappedRlp);
        rlpStream.Reset();
        rlpStream.SkipLength();
        int nullCount = 0;
        int i = 0;
        while (i < 16)
        {
            ulong val = Unsafe.ReadUnaligned<ulong>(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(rlpStream.Data), rlpStream.Position));
            int emptyCount = Math.Min(
                BitOperations.TrailingZeroCount(val ^ 0x8080808080808080UL) / 8,
                16 - i);
            if (emptyCount > 0)
            {
                rlpStream.Position += emptyCount;
                nullCount += emptyCount;
                i += emptyCount;
                continue;
            }

            int prefix = rlpStream.PeekByte();
            if (prefix == 128 || prefix == 0)
            {
                rlpStream.Position++;
                nullCount++;
            }
            else
            {
                rlpStream.SkipItem();
            }
            i++;
        }
        return nullCount;
    }

    // ── DecodeRlp ──

    [Benchmark]
    public void DecodeRlp_Baseline()
    {
        TrieNode node = new(NodeType.Unknown, _branchRlp);
        TreePath path = TreePath.Empty;
        node.ResolveNode(NullTrieNodeResolver.Instance, path);
    }

    [Benchmark]
    public void DecodeRlp_WithHint()
    {
        TrieNode node = new(NodeType.Unknown, _branchRlp);
        TreePath path = TreePath.Empty;
        node.ResolveNode(NullTrieNodeResolver.Instance, path, ReadFlags.HintStateTrie);
    }

    private static byte[] BuildBranchRlp(int populatedCount)
    {
        int contentLength = (16 - populatedCount) * 1 + populatedCount * 33 + 1;

        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));
        rlpStream.StartSequence(contentLength);

        int step = populatedCount > 0 ? 16 / populatedCount : 0;
        int nextPopulated = 0;
        int placed = 0;

        for (int i = 0; i < 16; i++)
        {
            if (placed < populatedCount && i == nextPopulated)
            {
                byte[] hash = Keccak.Compute([(byte)i]).Bytes.ToArray();
                rlpStream.Encode(hash);
                placed++;
                nextPopulated = step > 0 ? nextPopulated + step : i + 1;
                if (nextPopulated >= 16 && placed < populatedCount)
                {
                    nextPopulated = 16 - (populatedCount - placed);
                }
            }
            else
            {
                rlpStream.Encode(Array.Empty<byte>());
            }
        }

        rlpStream.Encode(Array.Empty<byte>());
        return rlpStream.Data.ToArray()!;
    }
}

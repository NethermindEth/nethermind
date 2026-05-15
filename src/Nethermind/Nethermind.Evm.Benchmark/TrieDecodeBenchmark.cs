// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core.Buffers;
using Nethermind.Trie;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures per-call allocation cost of <see cref="TrieNode.ResolveNode"/> (i.e., <c>DecodeRlp</c>)
/// for the three node types: branch, leaf, and extension.
///
/// <para>
/// Each BDN iteration pre-creates N fresh <c>NodeType.Unknown</c> nodes with pre-set RLP in
/// <see cref="IterationSetup"/> (allocations there are NOT counted by the memory diagnoser).
/// The benchmark body calls <see cref="TrieNode.ResolveNode"/> which triggers <c>DecodeRlp</c>
/// and allocates <c>BranchData</c> / <c>LeafData</c> / <c>ExtensionData</c>.
/// After decoding, <see cref="TrieNode.ReturnNodeDataToPool"/> returns the node data to the
/// thread-local pool so that subsequent measured iterations rent instead of allocate.
/// </para>
///
/// <para>Baseline run (no pool) vs pool run (after patch) should show ~144 B/decode reduction
/// for branch nodes.</para>
/// </summary>
[Config(typeof(TrieDecodeConfig))]
[MemoryDiagnoser]
public class TrieDecodeBenchmark
{
    /// <summary>
    /// Number of nodes decoded per iteration. Large enough to amortize BDN overhead,
    /// small enough for IterationSetup to be fast.
    /// </summary>
    private const int N = 2_000;

    private class TrieDecodeConfig : ManualConfig
    {
        public TrieDecodeConfig() =>
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(1)
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcForce(true));
    }

    private CappedArray<byte> _branchRlp;
    private CappedArray<byte> _leafRlp;
    private CappedArray<byte> _extensionRlp;

    // Pre-allocated arrays — IterationSetup fills them with fresh Unknown nodes
    private TrieNode[] _branchNodes = null!;
    private TrieNode[] _leafNodes   = null!;
    private TrieNode[] _extNodes    = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _branchRlp    = new CappedArray<byte>(BuildBranchRlp());
        _leafRlp      = new CappedArray<byte>(BuildLeafRlp());
        _extensionRlp = new CappedArray<byte>(BuildExtensionRlp());

        _branchNodes = new TrieNode[N];
        _leafNodes   = new TrieNode[N];
        _extNodes    = new TrieNode[N];
    }

    /// <summary>
    /// Resets every slot to a fresh Unknown node with pre-set RLP.
    /// BDN excludes these allocations from the "Allocated" measurement.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        for (int i = 0; i < N; i++)
        {
            _branchNodes[i] = new TrieNode(NodeType.Unknown, _branchRlp);
            _leafNodes[i]   = new TrieNode(NodeType.Unknown, _leafRlp);
            _extNodes[i]    = new TrieNode(NodeType.Unknown, _extensionRlp);
        }
    }

    /// <summary>
    /// Decode N branch nodes.
    /// Baseline: allocates <c>new BranchData()</c> each call (~144 B overhead per node).
    /// After OP-3 patch: rents from thread-local pool after warmup → ~0 B per node.
    /// <see cref="TrieNode.ReturnNodeDataToPool"/> returns the data to pool so the next
    /// iteration can rent instead of allocate.
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public void BranchDecode()
    {
        TreePath path = TreePath.Empty;
        for (int i = 0; i < N; i++)
        {
            _branchNodes[i].ResolveNode(null!, in path);
            _branchNodes[i].ReturnNodeDataToPool();
        }
    }

    /// <summary>Decode N leaf nodes. Key byte[] allocation is the main cost.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void LeafDecode()
    {
        TreePath path = TreePath.Empty;
        for (int i = 0; i < N; i++)
        {
            _leafNodes[i].ResolveNode(null!, in path);
            _leafNodes[i].ReturnNodeDataToPool();
        }
    }

    /// <summary>Decode N extension nodes.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void ExtensionDecode()
    {
        TreePath path = TreePath.Empty;
        for (int i = 0; i < N; i++)
        {
            _extNodes[i].ResolveNode(null!, in path);
            _extNodes[i].ReturnNodeDataToPool();
        }
    }

    // ── RLP builders ────────────────────────────────────────────────────────

    /// <summary>Branch node: 16 child hashes (32 bytes each) + empty value.</summary>
    private static byte[] BuildBranchRlp()
    {
        int payloadLen = 16 * 33 + 1; // 529 bytes
        byte[] result = new byte[3 + payloadLen];
        result[0] = 0xf9;
        result[1] = (byte)(payloadLen >> 8);
        result[2] = (byte)(payloadLen & 0xff);
        int pos = 3;
        for (int i = 0; i < 16; i++)
        {
            result[pos++] = 0xa0;
            for (int b = 0; b < 32; b++) result[pos++] = (byte)((i * 13 + b * 7) & 0xff);
        }
        result[pos] = 0x80; // empty value
        return result;
    }

    /// <summary>
    /// Leaf node: 8-nibble key (even, isLeaf=true) + 5-byte value.
    /// HexPrefix for [0,1,2,3,4,5,6,7] (even, leaf) = [0x20,0x01,0x23,0x45,0x67].
    /// RLP: list(0xcc) [ key_bytes(0x85 + 5 bytes) | value_bytes(0x85 + 5 bytes) ]
    /// </summary>
    // key: 0x85 0x20 0x01 0x23 0x45 0x67 (HexPrefix 8 nibbles even leaf, 6 bytes)
    // value: 0x85 0xAA 0xBB 0xCC 0xDD 0xEE (5-byte RLP string, 6 bytes)
    // list prefix 0xcc = 0xc0 + 12
    private static byte[] BuildLeafRlp() =>
        [0xcc, 0x85, 0x20, 0x01, 0x23, 0x45, 0x67, 0x85, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE];

    /// <summary>
    /// Extension node: 8-nibble key (even, isLeaf=false) + 32-byte child hash reference.
    /// HexPrefix for [0,1,2,3,4,5,6,7] (even, non-leaf) = [0x00,0x01,0x23,0x45,0x67].
    /// RLP: list(0xe7) [ key_bytes(0x85 + 5) | child_hash(0xa0 + 32 bytes) ]
    /// </summary>
    private static byte[] BuildExtensionRlp()
    {
        // key: 0x85 0x00 0x01 0x23 0x45 0x67 (6 bytes)
        // child hash: 0xa0 + 32 bytes (33 bytes total)
        // payload: 6 + 33 = 39 bytes → list prefix 0xe7 (0xc0+39)
        byte[] result = new byte[40];
        result[0] = 0xe7;
        result[1] = 0x85; result[2] = 0x00; result[3] = 0x01;
        result[4] = 0x23; result[5] = 0x45; result[6] = 0x67;
        result[7] = 0xa0;
        for (int i = 0; i < 32; i++) result[8 + i] = (byte)(i + 1);
        return result;
    }
}

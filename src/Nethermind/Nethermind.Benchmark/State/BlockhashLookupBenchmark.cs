// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.State;

/// <summary>The three ways BLOCKHASH can resolve an EIP-2935 ring-buffer entry.</summary>
/// <remarks>The opcode only needs the bytes, so the <see cref="Hash256"/> the first one builds is
/// discarded by the next instruction. The block-tree path is not measured here — every input to it is
/// loop-invariant, so a micro-loop hoists the lookup out; EvmOpcodesBenchmark covers it end to end.</remarks>
[MemoryDiagnoser]
public class BlockhashLookupBenchmark
{
    private const int OperationsPerInvoke = 1000;

    private BlockhashProvider _provider = null!;
    private BlockhashStore _store = null!;
    private BlockHeader _header = null!;
    private IReleaseSpec _spec = null!;
    private ulong _number;

    [GlobalSetup]
    public void Setup()
    {
        _spec = new ReleaseSpec
        {
            IsEip2935Enabled = true,
            IsEip7709Enabled = true,
            Eip2935RingBufferSize = Eip2935Constants.RingBufferSize
        };

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder builder = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(42);
        BlockTree tree = builder.TestObject;
        BlockHeader head = tree.FindHeader(41, BlockTreeLookupOptions.None)!;

        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (IDisposable seed = worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            stateRoot = worldState.StateRoot;
        }

        Block current = Build.A.Block.WithParent(head).WithStateRoot(stateRoot).TestObject;
        tree.SuggestHeader(current.Header);
        _header = current.Header;
        _number = _header.Number - 1;

        _provider = new BlockhashProvider(new BlockhashCache(builder.HeaderStore, LimboLogs.Instance), worldState, LimboLogs.Instance);
        _store = new BlockhashStore(worldState);

        worldState.BeginScope(_header);
        byte[] code = [1, 2, 3];
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(code), code, _spec);
        _store.ApplyBlockhashStateChanges(_header, _spec);

        _provider.TryGetBlockhash(_header, _number, _spec, out _);
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke, Baseline = true)]
    public int Allocating()
    {
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _store.GetBlockHashFromState(_header, _number, _spec) is null ? 0 : 1;
        return n;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int SpanFromState()
    {
        Span<byte> buffer = stackalloc byte[Hash256.Size];
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _store.TryGetBlockHashFromState(_header, _number, _spec, buffer) ? 1 : 0;
        return n;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int Memoized()
    {
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _provider.TryGetBlockhash(_header, _number, _spec, out _) ? 1 : 0;
        return n;
    }
}

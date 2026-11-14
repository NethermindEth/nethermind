// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures answering one <c>GetBlockBodies</c> request for stored blocks: reading them and serializing their bodies.
/// </summary>
[MemoryDiagnoser]
public class BlockBodiesServingBenchmarks
{
    private const int BlockCount = 128;

    private readonly BlockBodiesMessageSerializer _serializer = new();
    private IContainer _container = null!;
    private IBlockStore _store = null!;
    private (ulong Number, Hash256 Hash)[] _blocks = null!;

    [Params(150, 500)]
    public int TransactionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        _store = _container.Resolve<IBlockStore>();
        _blocks = new (ulong, Hash256)[BlockCount];
        for (int b = 0; b < BlockCount; b++)
        {
            Transaction[] transactions = new Transaction[TransactionCount];
            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithType(i % 3 == 0 ? TxType.Legacy : TxType.EIP1559)
                    .WithNonce((ulong)(b * TransactionCount + i))
                    .WithData(new byte[200])
                    .Signed()
                    .TestObject;
            }

            Block block = Build.A.Block.WithNumber((ulong)b + 1).WithTransactions(transactions).TestObject;
            _store.Insert(block);
            _blocks[b] = (block.Number, block.Hash!);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _container.Dispose();

    /// <summary>Decodes each stored block and encodes its body again, as serving did before.</summary>
    [Benchmark(Baseline = true)]
    public int Decoded()
    {
        BlockBody[] bodies = new BlockBody[BlockCount];
        for (int i = 0; i < BlockCount; i++)
        {
            bodies[i] = _store.Get(_blocks[i].Number, _blocks[i].Hash, RlpBehaviors.ExcludeHashes)!.Body;
        }

        // The serializer before this change: each body encoded straight into the message.
        int contentLength = 0;
        foreach (BlockBody body in bodies) contentLength += Rlp.LengthOfSequence(BlockBodyDecoder.Instance.GetBodyLength(body));
        int length = Rlp.LengthOfSequence(contentLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        RlpWriter writer = RlpWriter.ContentWriter(buffer.AsSpan(0, length));
        RlpSequence sequence = writer.StartSequence(contentLength);
        foreach (BlockBody body in bodies) BlockBodyDecoder.Instance.Encode(ref writer, body);
        writer.EndSequence(sequence);
        ArrayPool<byte>.Shared.Return(buffer);
        return length;
    }

    /// <summary>Copies each stored body as it is.</summary>
    [Benchmark]
    public int Encoded()
    {
        using ArrayPoolList<MemoryManager<byte>> owners = new(BlockCount);
        using ArrayPoolList<ReadOnlyMemory<byte>> blocks = new(BlockCount);
        for (int i = 0; i < BlockCount; i++)
        {
            MemoryManager<byte> block = _store.GetOwnedRlp(_blocks[i].Number, _blocks[i].Hash)!;
            owners.Add(block);
            blocks.Add(block.Memory);
        }

        using BlockBodiesMessage message = new() { Bodies = OwnedBlockBodies.FromEncodedBlocks(blocks) };
        foreach (IMemoryOwner<byte> owner in owners) owner.Dispose();
        int length = _serializer.GetLength(message);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        int written = _serializer.Serialize(buffer, message);
        ArrayPool<byte>.Shared.Return(buffer);
        return written;
    }
}

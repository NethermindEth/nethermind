// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using CkzgLib;
using DotNetty.Buffers;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Eth62 = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Eth63 = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Eth65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Eth66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Eth68 = Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Eth72 = Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures serializing eth messages into a packet the way the packet sender does, and
/// deserializing them from a DotNetty buffer the way the eth protocol handlers do.
/// </summary>
[MemoryDiagnoser]
public class EthMessageBenchmarks
{
    public enum Message
    {
        GetBlockHeaders66,
        BlockHeaders66,
        GetBlockBodies66,
        BlockBodies62,
        BlockBodies66,
        Receipts66,
        Receipts69,
        Receipts70,
        Transactions62,
        GetPooledTransactions66,
        PooledTransactions66,
        NodeData63,
        NodeData66,
        LargeNodeData66,
        NewPooledTransactionHashes68,
        Status69,
        BlockRangeUpdate69,
        NewPooledTransactionHashes72,
        GetCells72,
        Cells72
    }

    private const long RequestId = 0x1234_5678_9abc;
    private const ulong BlockNumber = 21_000_000;
    private static readonly BlobCellMask CellMask = BlobCellMask.FromIndices([4, 9, 21, 40, 63, 77, 100, 127]);

    private IContainer _container = null!;
    private Func<int> _serialize = null!;
    private Func<IByteBuffer, P2PMessage> _deserialize = null!;
    private IByteBuffer _body = null!;

    [ParamsAllValues]
    public Message Type { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.TestObject
        };
        _container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(chainSpec, new ConfigProvider(), LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, null))
            .Build();
        IMessageSerializationService service = _container.Resolve<IMessageSerializationService>();

        switch (Type)
        {
            case Message.GetBlockHeaders66:
                SetUp(service, new Eth66.GetBlockHeadersMessage(RequestId, new Eth62.GetBlockHeadersMessage { StartBlockHash = TestItem.KeccakA, MaxHeaders = 192 }));
                break;
            case Message.BlockHeaders66:
                ArrayPoolList<BlockHeader> headers = [with(192)];
                for (int i = 0; i < 192; i++)
                    headers.Add(Build.A.BlockHeader.WithNumber(BlockNumber + (ulong)i).WithParentHash(TestItem.KeccakA).TestObject);
                SetUp(service, new Eth66.BlockHeadersMessage(RequestId, new Eth62.BlockHeadersMessage(headers)));
                break;
            case Message.GetBlockBodies66:
                SetUp(service, new Eth66.GetBlockBodiesMessage(RequestId, new Eth62.GetBlockBodiesMessage(Hashes(128))));
                break;
            case Message.BlockBodies62:
                SetUp(service, new Eth62.BlockBodiesMessage(Blocks()));
                break;
            case Message.BlockBodies66:
                SetUp(service, new Eth66.BlockBodiesMessage(RequestId, new Eth62.BlockBodiesMessage(Blocks())));
                break;
            case Message.Receipts66:
                SetUp(service, new Eth66.ReceiptsMessage(RequestId, new Eth63.ReceiptsMessage(Receipts())));
                break;
            case Message.Receipts69:
                SetUp(service, new ReceiptsMessage69(RequestId, new ReceiptsInnerMessage69(Receipts())));
                break;
            case Message.Receipts70:
                SetUp(service, new ReceiptsMessage70(RequestId, Receipts(), lastBlockIncomplete: false));
                break;
            case Message.Transactions62:
                SetUp(service, new Eth62.TransactionsMessage(Transactions(64, blobTransactions: 0)));
                break;
            case Message.GetPooledTransactions66:
                ArrayPoolList<ValueHash256> pooledHashes = [with(256)];
                foreach (Hash256 hash in Hashes(256)) pooledHashes.Add(hash.ValueHash256);
                SetUp(service, new Eth66.GetPooledTransactionsMessage(pooledHashes) { RequestId = RequestId });
                break;
            case Message.PooledTransactions66:
                SetUp(service, new Eth66.PooledTransactionsMessage(RequestId, new Eth65.PooledTransactionsMessage(Transactions(16, blobTransactions: 4))));
                break;
            case Message.NodeData63:
                SetUp(service, new Eth63.NodeDataMessage(NodeData(384, 100)));
                break;
            case Message.NodeData66:
                SetUp(service, new Eth66.NodeDataMessage(RequestId, new Eth63.NodeDataMessage(NodeData(384, 100))));
                break;
            case Message.LargeNodeData66:
                SetUp(service, new Eth66.NodeDataMessage(RequestId, new Eth63.NodeDataMessage(NodeData(16, 24 * 1024))));
                break;
            case Message.NewPooledTransactionHashes68:
                ArrayPoolList<byte> types = [with(256)];
                ArrayPoolList<int> sizes = [with(256)];
                ArrayPoolList<ValueHash256> announcedHashes = [with(256)];
                foreach (Hash256 hash in Hashes(256))
                {
                    types.Add((byte)TxType.EIP1559);
                    sizes.Add(512);
                    announcedHashes.Add(hash.ValueHash256);
                }
                SetUp(service, new Eth68.NewPooledTransactionHashesMessage68(types, sizes, announcedHashes));
                break;
            case Message.Status69:
                SetUp(service, new StatusMessage69
                {
                    ProtocolVersion = 69,
                    NetworkId = 1,
                    GenesisHash = TestItem.KeccakA,
                    ForkId = new ForkId(0x9f3d2254, 0),
                    EarliestBlock = BlockNumber - 8192,
                    LatestBlock = BlockNumber,
                    LatestBlockHash = TestItem.KeccakB
                });
                break;
            case Message.BlockRangeUpdate69:
                SetUp(service, new BlockRangeUpdateMessage { EarliestBlock = BlockNumber - 8192, LatestBlock = BlockNumber, LatestBlockHash = TestItem.KeccakA });
                break;
            case Message.NewPooledTransactionHashes72:
                ValueHash256[] blobHashes = Array.ConvertAll(Hashes(256), static hash => hash.ValueHash256);
                SetUp(service, new Eth72.NewPooledTransactionHashesMessage72(
                    Array.ConvertAll(blobHashes, static _ => (byte)TxType.Blob), Array.ConvertAll(blobHashes, static _ => 131_072), blobHashes, CellMask.ToBytes()));
                break;
            case Message.GetCells72:
                SetUp(service, new Eth72.GetCellsMessage72(RequestId, Array.ConvertAll(Hashes(64), static hash => hash.ValueHash256), CellMask.ToBytes()));
                break;
            case Message.Cells72:
                SetUp(service, new Eth72.CellsMessage72(RequestId, Array.ConvertAll(Hashes(16), static hash => hash.ValueHash256), Cells(16, blobsPerTransaction: 2), CellMask.ToBytes()));
                break;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _body.Release();
        _container.Dispose();
    }

    [Benchmark]
    public int Serialize() => _serialize();

    [Benchmark]
    public int Deserialize()
    {
        _body.SetReaderIndex(0);
        using P2PMessage message = _deserialize(_body);
        ReturnTransactions(message);
        return message.PacketType;
    }

    // As the handlers do with the transactions they do not submit: disposing the message returns only their list.
    private static void ReturnTransactions(P2PMessage message)
    {
        IOwnedReadOnlyList<Transaction> transactions = message switch
        {
            Eth62.TransactionsMessage transactionsMessage => transactionsMessage.Transactions,
            Eth66.PooledTransactionsMessage pooledTransactionsMessage => pooledTransactionsMessage.EthMessage.Transactions,
            _ => null
        };
        if (transactions is null) return;

        foreach (Transaction transaction in transactions.AsSpan())
        {
            transaction.ClearPreHash();
            TxDecoder.TxObjectPool.Return(transaction);
        }
    }

    private void SetUp<T>(IMessageSerializationService service, T message) where T : P2PMessage
    {
        _serialize = () =>
        {
            IByteBuffer packet = service.ZeroSerialize(message);
            int length = packet.ReadableBytes;
            packet.Release();
            return length;
        };
        _deserialize = body => service.Deserialize<T>(body);

        IByteBuffer packet = service.ZeroSerialize(message);
        packet.SkipBytes(Rlp.LengthOf(message.AdaptivePacketType));
        _body = Unpooled.WrappedBuffer(packet.ReadAllBytesAsArray());
        packet.Release();
    }

    private static Hash256[] Hashes(int count)
    {
        Hash256[] hashes = new Hash256[count];
        for (int i = 0; i < count; i++)
            hashes[i] = Keccak.Compute(BitConverter.GetBytes(i));
        return hashes;
    }

    private static Block[] Blocks()
    {
        Block[] blocks = new Block[32];
        for (int i = 0; i < blocks.Length; i++)
        {
            Transaction[] transactions = new Transaction[50];
            for (int j = 0; j < transactions.Length; j++)
            {
                transactions[j] = Build.A.Transaction
                    .WithNonce((ulong)(i * transactions.Length + j))
                    .WithData(new byte[512])
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject;
            }

            blocks[i] = Build.A.Block.WithNumber(BlockNumber + (ulong)i).WithTransactions(transactions).TestObject;
        }

        return blocks;
    }

    private static ArrayPoolList<TxReceipt[]> Receipts()
    {
        ArrayPoolList<TxReceipt[]> receipts = [with(32)];
        for (int i = 0; i < 32; i++)
        {
            TxReceipt[] blockReceipts = new TxReceipt[50];
            for (int j = 0; j < blockReceipts.Length; j++)
            {
                blockReceipts[j] = Build.A.Receipt
                    .WithAllFieldsFilled
                    .WithBlockNumber(BlockNumber + (ulong)i)
                    .WithTxType(TxType.EIP1559)
                    .TestObject;
            }

            receipts.Add(blockReceipts);
        }

        return receipts;
    }

    private static ArrayPoolList<Transaction> Transactions(int count, int blobTransactions)
    {
        ArrayPoolList<Transaction> transactions = [with(count)];
        for (int i = 0; i < count; i++)
        {
            TransactionBuilder<Transaction> builder = Build.A.Transaction.WithNonce((ulong)i).WithData(new byte[512]);
            if (i < blobTransactions) builder = builder.WithShardBlobTxTypeAndFields();
            transactions.Add(builder.SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        }

        return transactions;
    }

    private static byte[][][] Cells(int transactions, int blobsPerTransaction)
    {
        byte[][][] cells = new byte[transactions][][];
        for (int i = 0; i < transactions; i++)
        {
            cells[i] = new byte[blobsPerTransaction * CellMask.Count][];
            for (int j = 0; j < cells[i].Length; j++)
                cells[i][j] = new byte[Ckzg.BytesPerCell];
        }

        return cells;
    }

    private static ByteArrayListAdapter NodeData(int count, int size)
    {
        ArrayPoolList<byte[]> nodes = [with(count)];
        for (int i = 0; i < count; i++)
        {
            byte[] node = new byte[size];
            node[0] = (byte)i;
            nodes.Add(node);
        }

        return new ByteArrayListAdapter(nodes);
    }
}

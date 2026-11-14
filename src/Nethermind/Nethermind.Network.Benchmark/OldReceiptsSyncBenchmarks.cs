// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Synchronization.FastBlocks;
using Eth63 = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// What old receipts sync does with one block's receipts from an eth/69 peer: read them from the message, check the
/// receipts root, load the block's transactions and store the receipts.
/// </summary>
[MemoryDiagnoser]
public class OldReceiptsSyncBenchmarks
{
    private static readonly ReceiptMessageDecoder69 Decoder69 = new();

    private IContainer _container = null!;
    private IReleaseSpec _spec = null!;
    private byte[] _message = null!;
    private ReceiptsMessageSerializer69 _serializer = null!;
    private Block _block = null!;
    private IBlockStore _blockStore = null!;
    private IReceiptStorage _receiptStorage = null!;

    [Params(150)]
    public int TransactionCount { get; set; }

    [Params(1, 4)]
    public int LogsPerReceipt { get; set; }

    [Params(false, true)]
    public bool Eip7668 { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _spec = Eip7668 ? new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true } : Bogota.Instance;
        _container = new ContainerBuilder().AddModule(new TestNethermindModule(_spec)).Build();
        ISpecProvider specProvider = _container.Resolve<ISpecProvider>();
        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        Transaction[] transactions = new Transaction[TransactionCount];
        TxReceipt[] receipts = new TxReceipt[TransactionCount];
        ulong gasUsedTotal = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithType(TxType.EIP1559).WithNonce((ulong)i)
                .Signed(ecdsa, TestItem.PrivateKeyA).TestObject;
            LogEntry[] logs = new LogEntry[LogsPerReceipt];
            for (int j = 0; j < logs.Length; j++)
            {
                logs[j] = new LogEntry(TestItem.AddressB, new byte[64], [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC]);
            }

            gasUsedTotal += 50_000;
            receipts[i] = Build.A.Receipt.WithTxType(TxType.EIP1559).WithStatusCode(StatusCode.Success)
                .WithGasUsedTotal(gasUsedTotal).WithLogs(logs).TestObject;
            receipts[i].Bloom = new Bloom(logs);
        }

        Hash256 receiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, _spec, null);
        _block = Build.A.Block.WithNumber(1).WithTransactions(transactions).WithReceiptsRoot(receiptsRoot).TestObject;

        _blockStore = _container.Resolve<IBlockStore>();
        _blockStore.Insert(_block);
        _receiptStorage = _container.Resolve<IReceiptStorage>();

        _serializer = new ReceiptsMessageSerializer69(specProvider);
        IMessageSerializer<ReceiptsMessage69> serializer = _serializer;
        using ReceiptsMessage69 message = new(1, new Eth63.ReceiptsMessage(new ArrayPoolList<TxReceipt[]>(1) { receipts }));
        _message = new byte[serializer.GetLength(message)];
        serializer.Serialize(_message, message);
    }

    [GlobalCleanup]
    public void Cleanup() => _container.Dispose();

    /// <summary>The receipts decoded as the message serializer decoded them before they were kept encoded.</summary>
    [Benchmark(Baseline = true)]
    public int Decoded()
    {
        RlpReader message = new(_message);
        RlpReader fields = message.EnterSequence(out _);
        fields.SkipItem();
        RlpReader blocks = fields.EnterSequence(out _);
        RlpReader block = blocks.EnterSequence(out int blockLength);
        TxReceipt[] receipts = new TxReceipt[TransactionCount];
        for (int i = 0; i < receipts.Length; i++) receipts[i] = Decoder69.Decode(ref block)!;
        blocks.ExitSequence(in block, blockLength);
        if (!_spec.IsEip7668Enabled) ReceiptsSyncFeed.NormalizeZeroBlooms(receipts);
        if (ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, _spec, _block.ReceiptsRoot) != _block.ReceiptsRoot)
            throw new InvalidOperationException("The receipts root does not match.");

        Block stored = _blockStore.Get(_block.Number, _block.Hash!, shouldCache: false)!;
        _receiptStorage.Insert(stored, receipts, ensureCanonical: true);
        return receipts.Length;
    }

    [Benchmark]
    public int Encoded()
    {
        using ReceiptsMessage69 message = ((IMessageSerializer<ReceiptsMessage69>)_serializer).Deserialize(_message);
        NetworkBlockReceipts receipts = (NetworkBlockReceipts)message.EthMessage.TxReceipts;
        ReadOnlyMemory<byte> encoded = receipts.GetEncoded(0);
        if (ReceiptsRootCalculator.Instance.TryGetReceiptsRoot(encoded.Span, receipts.WithBloom, _spec) != _block.ReceiptsRoot)
            throw new InvalidOperationException("The receipts root does not match.");

        Transaction[] transactions = BlockBodyDecoder.DecodeEncodedTransactionsOfBlock(_blockStore.GetRlp(_block.Number, _block.Hash!)!);
        if (!_receiptStorage.TryInsertNetworkReceipts(new Block(_block.Header, new BlockBody(transactions, [])), encoded, receipts.WithBloom, ensureCanonical: true))
            throw new InvalidOperationException("The receipt store does not take encoded receipts.");
        foreach (Transaction transaction in transactions) TxDecoder.TxObjectPool.Return(transaction);
        return transactions.Length;
    }
}

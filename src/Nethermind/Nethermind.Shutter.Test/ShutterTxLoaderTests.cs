// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Crypto;
using NSubstitute;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using System.Linq;
using Nethermind.Specs.Forks;
using Nethermind.Specs;
using Nethermind.Shutter.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Abi;
using System.Threading.Tasks;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Blockchain.Receipts;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Core.Crypto;

namespace Nethermind.Shutter.Test;

using static Nethermind.Merge.AuRa.Test.AuRaMergeEngineModuleTests;

[TestFixture]
class ShutterTxLoaderSourceTests : EngineModuleTests
{
    private readonly IEthereumEcdsa _ecdsa = new EthereumEcdsa(BlockchainIds.Chiado);
    private readonly AbiEncoder _abiEncoder = new();
    private static readonly ShutterConfig _cfg = new()
    {
        SequencerContractAddress = "0x0000000000000000000000000000000000000000",
        EncryptedGasLimit = 1000000
    };

    // [SetUp]
    // public void Setup()
    // {
    // }

    [Test]
    public async Task Can_load_transactions()
    {
        const ulong initialSlot = 16082024;
        const ulong initialTxPointer = 1000;
        Random rnd = new Random(100);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);

        ShutterTxLoader txLoader = new(
            chain.LogFinder,
            _cfg,
            ChiadoSpecProvider.Instance,
            _ecdsa,
            LimboLogs.Instance
        );
        
        ShutterEventEmitter eventEmitter = new(
            rnd,
            BlockchainIds.Chiado,
            0,
            initialTxPointer,
            _abiEncoder,
            txLoader._sequencerContract.ContractAddress!,
            txLoader._sequencerContract._transactionSubmittedAbi
        );
        IEnumerable<ShutterEventEmitter.Event> eventSource = eventEmitter.EmitEvent();

        for (int i = 0; i < 10; i++)
        {
            // todo: fix inserting when more than 1 log
            List<ShutterEventEmitter.Event> events = eventSource.Take(1).ToList();
            List<(byte[], byte[])> keys = events.Select(e => (e.IdentityPreimage, e.Key)).ToList();
            keys.Insert(0, ([], [])); // placeholder key
            LogEntry[] logs = events.Select(e => e.LogEntry).ToArray();
        
            Block head = chain.BlockTree.Head!;
            TxReceipt[] receipts = InsertShutterReceipts(rnd, chain.ReceiptStorage, head, logs);
            txLoader.LoadFromReceipts(head, receipts);

            ShutterTransactions txs = txLoader.LoadTransactions(head, new() {
                Slot = initialSlot,
                Eon = 0,
                TxPointer = initialTxPointer,
                Keys = keys
            });

            Assert.That(txs.Transactions, Has.Length.EqualTo(1));
            // advance chain
        }
        // LogEntry shutterLog = e.LogEntry;

        // // add Shutter events to head block receipts
        // Block head = chain.BlockTree.Head!;
        // InsertShutterReceipts(chain.ReceiptStorage, head, [shutterLog]);

        // // scan receipts for Shutter logs
        // ShutterTransactions txs = txLoader.LoadTransactions(head, new() {
        //     Slot = initialSlot,
        //     Eon = 0,
        //     TxPointer = initialTxPointer,
        //     Keys = [([], []), (e.IdentityPreimage, e.Key)]
        // });

        // Assert.That(txs.Transactions, Has.Length.EqualTo(1));
        // Assert.That(Rlp.Encode<Transaction>(txs.Transactions[0]).Bytes, Is.EqualTo(e.Transaction));

        // e = events.Take(1).ElementAt(0);
        // shutterLog = e.LogEntry;

        // head = chain.BlockTree.Head!;
        // TxReceipt[] receipts = InsertShutterReceipts(chain.ReceiptStorage, head, [shutterLog]);
        // txLoader.LoadFromReceipts(head, receipts);

        // txs = txLoader.LoadTransactions(head, new() {
        //     Slot = initialSlot + 1,
        //     Eon = 0,
        //     TxPointer = initialTxPointer + 1,
        //     Keys = [([], []), (e.IdentityPreimage, e.Key)]
        // });

        // Assert.That(txs.Transactions, Has.Length.EqualTo(1));
        // Assert.That(Rlp.Encode<Transaction>(txs.Transactions[0]).Bytes, Is.EqualTo(e.Transaction));
    }

    private static TxReceipt[] InsertShutterReceipts(Random rnd, IReceiptStorage receiptStorage, Block block, in LogEntry[] logs)
    {
        var receipts = new TxReceipt[logs.Length];
        block.Header.Bloom = new(logs);

        // one log per receipt
        for (int i = 0; i < logs.Length; i++)
        {
            var h = new byte[32];
            rnd.NextBytes(h);
            receipts[i] = Build.A.Receipt
                .WithLogs([logs[i]])
                .WithTransactionHash(new(h))
                .WithBlockHash(block.Hash)
                .WithBlockNumber(block.Number)
                .TestObject;
        }

        receiptStorage.Insert(block, receipts);
        return receipts;
    }

    // [Test]
    // public async Task Can_load_events_from_scanning_logs()
    // {
    //     using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
    //     IEngineRpcModule rpc = CreateEngineModule(chain);
    //     IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);

    //     ShutterTxLoader txLoader = new(
    //         chain.LogFinder,
    //         _cfg,
    //         GnosisSpecProvider.Instance,
    //         _ecdsa,
    //         LimboLogs.Instance
    //     );

    //     byte[] encryptedMessage = [0xaa, 0xbb];
    //     LogEntry shutterLog = EncodeShutterLog(txLoader, 0, 1000, new(), Address.Zero, encryptedMessage, new());

    //     Block head = chain.BlockTree.Head!;
    //     head.Header.Bloom = new([shutterLog]);

    //     TxReceipt receipt = Build.A.Receipt.WithLogs([shutterLog]).WithTransactionHash(head.Hash).TestObject;
    //     chain.ReceiptStorage.Insert(head, [receipt]);

    //     List<SequencedTransaction> txs = txLoader.GetNextTransactions(0, 1000, chain.BlockTree.Head!.Number).ToList();
    //     Assert.That(txs, Has.Count.EqualTo(1));
    //     Assert.That(txs[0].EncryptedTransaction, Is.EqualTo(encryptedMessage));
    // }

    // [Test]
    // public void Can_load_events_from_receipts()
    // {
    //     ShutterTxLoader txLoader = new(
    //         Substitute.For<ILogFinder>(),
    //         _cfg,
    //         GnosisSpecProvider.Instance,
    //         _ecdsa,
    //         LimboLogs.Instance
    //     )
    //     {
    //         _loadFromReceipts = true,
    //         _txPointer = 1000
    //     };

    //     TxReceipt[] receipts = [];

    //     // no Shutter logs
    //     txLoader.LoadFromReceipts(Build.A.Block.TestObject, receipts);
    //     Assert.That(txLoader._txPointer, Is.EqualTo(1000));

    //     // one Shutter log
    //     LogEntry shutterLog = EncodeShutterLog(txLoader, 0, 1000, new(), Address.Zero, [], new());
    //     TxReceipt receipt = Build.A.Receipt.WithLogs([shutterLog]).TestObject;
    //     receipts = [receipt];

    //     txLoader.LoadFromReceipts(Build.A.Block.TestObject, receipts);

    //     ISequencerContract.TransactionSubmitted expected = new()
    //     {
    //         Eon = 0,
    //         TxIndex = 1000,
    //         IdentityPrefix = new(),
    //         Sender = Address.Zero,
    //         EncryptedTransaction = [],
    //         GasLimit = new()
    //     };
    //     Assert.That(txLoader._txPointer, Is.EqualTo(1001));
    //     Assert.That(txLoader._transactionSubmittedEvents.Peek().Equals(expected));
    // }

    // [Test]
    // public void Can_decrypt_sequenced_transactions()
    // {
    //     const int txCount = 100;
    //     UInt256 sk = 4328942385;
    //     Random rnd = new Random(100);

    //     ShutterTxLoader txLoader = new(
    //         Substitute.For<ILogFinder>(),
    //         _cfg,
    //         GnosisSpecProvider.Instance,
    //         _ecdsa,
    //         LimboLogs.Instance
    //     );

    //     ShutterEventEmitter eventEmitter = new(
    //         new Random(100),
    //         BlockchainIds.Gnosis,
    //         0,
    //         0,
    //         new(),
    //         Address.Zero,
    //     );

    //     byte[] msg = Convert.FromHexString("f869820248849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd3a001e44318458b1f279bf81aef969df1b9991944bf8b9d16fd1799ed5b0a7986faa058f572cce63aaff3326df9c902d338b0c416c8fb93109446d6aadd5a65d3d115");

    //     List<SequencedTransaction> sequencedTransactions = [];
    //     List<(byte[] IdentityPreimage, byte[] Key)> keys = [];

    //     for (int i = 0; i < txCount; i++)
    //     {
    //         byte[] identityPreimage = new byte[52];
    //         byte[] sigma = new byte[32];
    //         rnd.NextBytes(identityPreimage);
    //         rnd.NextBytes(sigma);

    //         (byte[] encryptedMessage, (byte[], byte[]) key) = GenerateEncryptedMessage(msg, identityPreimage, sk, new(sigma));
    //         SequencedTransaction sequencedTransaction = new()
    //         {
    //             Index = i,
    //             Eon = 0,
    //             EncryptedTransaction = encryptedMessage,
    //             GasLimit = 0,
    //             Identity = ShutterCrypto.ComputeIdentity(identityPreimage),
    //             IdentityPreimage = identityPreimage
    //         };
    //         sequencedTransactions.Add(sequencedTransaction);
    //         keys.Add(key);
    //     }

    //     // decryption keys are sorted by preimage
    //     keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
    //     keys.Insert(0, new());

    //     Transaction[] txs = txLoader.DecryptSequencedTransactions(sequencedTransactions, keys);
    //     foreach (Transaction tx in txs)
    //     {
    //         byte[] tmp = Rlp.Encode<Transaction>(tx).Bytes;
    //         Assert.That(Enumerable.SequenceEqual(tmp, msg));
    //     }
    //     Assert.That(txs, Has.Length.EqualTo(txCount));
    // }

    [Test]
    [TestCase(2,
        new string[] {
            // valid
            "f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a0df1cd95e75d0188cded14137c9c83a3ce6d710886bf9139a10cab20dd693ab85a020f5fdae2704bf133be02897c886ceb9189a9ea363989b11330461a78b9bb368",
            "02f8758227d81385012a05f20085012a05f2088252089497d2eeb65da0c37dc0f43ff4691e521673efadfd872386f26fc1000080c080a0c00874a71afda5444b961f78774196fbb833c33482d6463b97380147dd7d472fa061d508b02cb212c78d0b864a04b10e0b0e3accca6e08252049d999c4629cd9a8",
            // wrong chain id
            "f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd5a0b806b9e17c30c4eaad51b290714a407925c82818311a432e4ee656ad23938852a045cd3f087a1f2580ba7d806fa6ba2bfc9933b317ee89fa67713665aab7c22441",
            // bad signature
            "f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a09999999999999999999999999999999999999999999999999999999999999999a09999999999999999999999999999999999999999999999999999999999999999"
        }
    )]
    public void Can_filter_invalid_transactions(int expectedValid, string[] transactionHexes)
    {
        ShutterTxLoader txLoader = new(
            Substitute.For<ILogFinder>(),
            _cfg,
            ChiadoSpecProvider.Instance,
            _ecdsa,
            LimboLogs.Instance
        );

        Transaction[] transactions = new Transaction[transactionHexes.Length];
        for (int i = 0; i < transactionHexes.Length; i++)
        {
            Transaction tx = Rlp.Decode<Transaction>(Convert.FromHexString(transactionHexes[i]));
            tx.SenderAddress = _ecdsa.RecoverAddress(tx, true);
            transactions[i] = tx;
        }

        IReleaseSpec releaseSpec = Cancun.Instance;
        IEnumerable<Transaction> filtered = txLoader.FilterTransactions(transactions, releaseSpec);
        Assert.That(filtered.Count, Is.EqualTo(expectedValid));
    }
}

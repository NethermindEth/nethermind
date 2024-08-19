// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Crypto;
using Nethermind.Blockchain.Find;
using Nethermind.Logging;
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
using Nethermind.Core.Extensions;

namespace Nethermind.Shutter.Test;

using static Nethermind.Merge.AuRa.Test.AuRaMergeEngineModuleTests;

[TestFixture]
class ShutterTxLoaderTests : EngineModuleTests
{
    private readonly IEthereumEcdsa _ecdsa = new EthereumEcdsa(BlockchainIds.Chiado);
    private readonly AbiEncoder _abiEncoder = new();
    private static readonly ShutterConfig _cfg = new()
    {
        SequencerContractAddress = "0x0000000000000000000000000000000000000000",
        EncryptedGasLimit = 1000000
    };

    [Test]
    public async Task Can_load_transactions()
    {
        ulong slot = 16082024;
        ulong txPointer = 1000;
        Random rnd = new Random(100);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

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
            txPointer,
            _abiEncoder,
            txLoader._sequencerContract.ContractAddress!,
            txLoader._sequencerContract._transactionSubmittedAbi
        );
        IEnumerable<ShutterEventEmitter.Event> eventSource = eventEmitter.EmitEvents();

        for (int i = 0; i < 10; i++)
        {
            List<ShutterEventEmitter.Event> events = eventSource.Take(20).ToList();
            LogEntry[] logs = events.Select(e => e.LogEntry).ToArray();

            List<(byte[] IdentityPreimage, byte[] Key)> keys = events.Select(e => (e.IdentityPreimage, e.Key)).ToList();
            // decryption keys are sorted by preimage
            keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
            keys.Insert(0, ([], [])); // placeholder key
        
            Block head = chain.BlockTree.Head!;
            BlockHeader parentHeader = chain.BlockTree.FindParentHeader(head.Header, Blockchain.BlockTreeLookupOptions.None)!;
            TxReceipt[] receipts = InsertShutterReceipts(rnd, chain.ReceiptStorage, head, logs);
            txLoader.LoadFromReceipts(head, receipts);

            // check receipts inserted
            Assert.That(chain.ReceiptStorage.Get(head), Is.EqualTo(receipts));

            ShutterTransactions txs = txLoader.LoadTransactions(head, parentHeader, new() {
                Slot = slot++,
                Eon = 0,
                TxPointer = txPointer,
                Keys = keys
            });
            // simulate keypers increasing txPointer
            txPointer += 20;

            Assert.That(txs.Transactions, Has.Length.EqualTo(20));

            IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
            lastPayload = payloads[0];
        }
    }

    // [Test]
    // public async Task Can_load_and_filter_transactions()
    // {
    //     const ulong initialSlot = 16082024;
    //     const ulong initialTxPointer = 1000;
    //     Random rnd = new Random(100);

    //     using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
    //     IEngineRpcModule rpc = CreateEngineModule(chain);
    //     IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
    //     ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

    //     ShutterTxLoader txLoader = new(
    //         chain.LogFinder,
    //         _cfg,
    //         ChiadoSpecProvider.Instance,
    //         _ecdsa,
    //         LimboLogs.Instance
    //     );
        
    //     ShutterEventEmitter eventEmitter = new(
    //         rnd,
    //         BlockchainIds.Chiado,
    //         0,
    //         initialTxPointer,
    //         _abiEncoder,
    //         txLoader._sequencerContract.ContractAddress!,
    //         txLoader._sequencerContract._transactionSubmittedAbi
    //     );
    //     IEnumerable<ShutterEventEmitter.Event> eventSource = eventEmitter.EmitEvents();

    //     for (int i = 0; i < 10; i++)
    //     {
    //         List<ShutterEventEmitter.Event> events = eventSource.Take(20).ToList();
    //         LogEntry[] logs = events.Select(e => e.LogEntry).ToArray();

    //         List<(byte[] IdentityPreimage, byte[] Key)> keys = events.Select(e => (e.IdentityPreimage, e.Key)).ToList();
    //         // decryption keys are sorted by preimage
    //         keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
    //         keys.Insert(0, ([], [])); // placeholder key
        
    //         Block head = chain.BlockTree.Head!;
    //         BlockHeader parentHeader = chain.BlockTree.FindParentHeader(head.Header, Blockchain.BlockTreeLookupOptions.None)!;
    //         TxReceipt[] receipts = InsertShutterReceipts(rnd, chain.ReceiptStorage, head, logs);
    //         txLoader.LoadFromReceipts(head, receipts);

    //         // check receipts inserted
    //         Assert.That(chain.ReceiptStorage.Get(head), Is.EqualTo(receipts));

    //         ShutterTransactions txs = txLoader.LoadTransactions(head, parentHeader, new() {
    //             Slot = initialSlot,
    //             Eon = 0,
    //             TxPointer = initialTxPointer,
    //             Keys = keys
    //         });

    //         Assert.That(txs.Transactions, Has.Length.EqualTo(20));

    //         IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
    //         lastPayload = payloads[0];
    //     }
    // }

    // [Test]
    // public async Task Can_load_up_to_gas_limit()
    // {
    //     const ulong initialSlot = 16082024;
    //     const ulong initialTxPointer = 1000;
    //     Random rnd = new Random(100);

    //     using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
    //     IEngineRpcModule rpc = CreateEngineModule(chain);
    //     IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
    //     ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

    //     ShutterTxLoader txLoader = new(
    //         chain.LogFinder,
    //         _cfg,
    //         ChiadoSpecProvider.Instance,
    //         _ecdsa,
    //         LimboLogs.Instance
    //     );
        
    //     ShutterEventEmitter eventEmitter = new(
    //         rnd,
    //         BlockchainIds.Chiado,
    //         0,
    //         initialTxPointer,
    //         _abiEncoder,
    //         txLoader._sequencerContract.ContractAddress!,
    //         txLoader._sequencerContract._transactionSubmittedAbi
    //     );
    //     IEnumerable<ShutterEventEmitter.Event> eventSource = eventEmitter.EmitEvents();

    //     for (int i = 0; i < 10; i++)
    //     {
    //         List<ShutterEventEmitter.Event> events = eventSource.Take(20).ToList();
    //         LogEntry[] logs = events.Select(e => e.LogEntry).ToArray();

    //         List<(byte[] IdentityPreimage, byte[] Key)> keys = events.Select(e => (e.IdentityPreimage, e.Key)).ToList();
    //         // decryption keys are sorted by preimage
    //         keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
    //         keys.Insert(0, ([], [])); // placeholder key
        
    //         Block head = chain.BlockTree.Head!;
    //         BlockHeader parentHeader = chain.BlockTree.FindParentHeader(head.Header, Blockchain.BlockTreeLookupOptions.None)!;
    //         TxReceipt[] receipts = InsertShutterReceipts(rnd, chain.ReceiptStorage, head, logs);
    //         txLoader.LoadFromReceipts(head, receipts);

    //         // check receipts inserted
    //         Assert.That(chain.ReceiptStorage.Get(head), Is.EqualTo(receipts));

    //         ShutterTransactions txs = txLoader.LoadTransactions(head, parentHeader, new() {
    //             Slot = initialSlot,
    //             Eon = 0,
    //             TxPointer = initialTxPointer,
    //             Keys = keys
    //         });

    //         Assert.That(txs.Transactions, Has.Length.EqualTo(20));

    //         IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
    //         lastPayload = payloads[0];
    //     }
    // }
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
}

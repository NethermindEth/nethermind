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
    const int _seed = 100;
    const ulong _initialSlot = 16082024;
    const ulong _initialTxPointer = 1000;
    private readonly IEthereumEcdsa _ecdsa = new EthereumEcdsa(BlockchainIds.Chiado);
    private readonly AbiEncoder _abiEncoder = new();
    private static readonly ShutterConfig _cfg = new()
    {
        SequencerContractAddress = "0x0000000000000000000000000000000000000000",
        EncryptedGasLimit = 21000 * 20
    };

    [Test]
    public async Task Can_load_transactions_over_slots()
    {
        Random rnd = new(_seed);
        ulong slot = _initialSlot;
        ulong txPointer = _initialTxPointer;

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterTxLoader txLoader = InitTxLoader(chain.LogFinder);
        IEnumerable<ShutterEventEmitter.Event> eventSource = EmitEvents(rnd, 0, txPointer, txLoader.GetAbi());

        for (int i = 0; i < 10; i++)
        {
            (LogEntry[] Logs, List<(byte[] IdentityPreimage, byte[] Key)> Keys) events = GetFromEventsSource(eventSource, 20);
        
            ShutterTransactions txs = OnNewLogs(rnd, chain, txLoader, events.Logs, new() {
                Slot = slot++,
                Eon = 0,
                TxPointer = txPointer,
                Keys = events.Keys
            });
            txPointer += 20;

            Assert.That(txs.Transactions, Has.Length.EqualTo(20));

            IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
            lastPayload = payloads[0];
        }
    }

    [Test]
    public async Task Can_load_and_filter_transactions()
    {
        Random rnd = new(_seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterTxLoader txLoader = InitTxLoader(chain.LogFinder);
        IEnumerable<ShutterEventEmitter.Event> eventSource = EmitHalfInvalidEvents(rnd, _initialTxPointer, txLoader.GetAbi());

        (LogEntry[] Logs, List<(byte[] IdentityPreimage, byte[] Key)> Keys) events = GetFromEventsSource(eventSource, 20);
    
        Block head = chain.BlockTree.Head!;
        BlockHeader parentHeader = chain.BlockTree.FindParentHeader(head.Header, Blockchain.BlockTreeLookupOptions.None)!;
        TxReceipt[] receipts = InsertShutterReceipts(rnd, chain.ReceiptStorage, head, events.Logs);
        txLoader.LoadFromReceipts(head, receipts);

        ShutterTransactions txs = OnNewLogs(rnd, chain, txLoader, events.Logs, new() {
            Slot = _initialSlot,
            Eon = 0,
            TxPointer = _initialTxPointer,
            Keys = events.Keys
        });

        // half of transactions were invalid, should have been filtered
        Assert.That(txs.Transactions, Has.Length.EqualTo(10));
    }

    [Test]
    public async Task Can_load_up_to_gas_limit()
    {
        Random rnd = new(_seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterTxLoader txLoader = InitTxLoader(chain.LogFinder);
        IEnumerable<ShutterEventEmitter.Event> eventSource = EmitEvents(rnd, 0, _initialTxPointer, txLoader.GetAbi());

        List<ShutterEventEmitter.Event> events = eventSource.Take(40).ToList();
        LogEntry[] logs = events.Select(e => e.LogEntry).ToArray();

        List<(byte[] IdentityPreimage, byte[] Key)> keys = events.Select(e => (e.IdentityPreimage, e.Key)).ToList();

        List<(byte[] IdentityPreimage, byte[] Key)> slot1Keys = keys[..20];
        slot1Keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));

        List<(byte[] IdentityPreimage, byte[] Key)> slot2Keys = keys[20..];
        slot2Keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
    
        ShutterTransactions txs = OnNewLogs(rnd, chain, txLoader, logs, new() {
            Slot = _initialSlot,
            Eon = 0,
            TxPointer = _initialTxPointer,
            Keys = slot1Keys
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(20));

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
        lastPayload = payloads[0];

        txs = OnNewLogs(rnd, chain, txLoader, [], new() {
            Slot = _initialSlot + 1,
            Eon = 0,
            TxPointer = _initialTxPointer + 20,
            Keys = slot2Keys
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(20));

        payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
        lastPayload = payloads[0];

        txs = OnNewLogs(rnd, chain, txLoader, [], new() {
            Slot = _initialSlot + 2,
            Eon = 0,
            TxPointer = _initialTxPointer + 40,
            Keys = []
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(0));
    }

    [Test]
    public async Task Can_load_transactions_over_eons()
    {
        Random rnd = new(_seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterTxLoader txLoader = InitTxLoader(chain.LogFinder);

        IEnumerable<ShutterEventEmitter.Event> eventSource = EmitEvents(rnd, 0, _initialTxPointer, txLoader.GetAbi());
        (LogEntry[] Logs, List<(byte[] IdentityPreimage, byte[] Key)> Keys) events = GetFromEventsSource(eventSource, 5);
    
        ShutterTransactions txs = OnNewLogs(rnd, chain, txLoader, events.Logs, new() {
            Slot = _initialSlot,
            Eon = 0,
            TxPointer = _initialTxPointer,
            Keys = events.Keys
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(5));

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
        lastPayload = payloads[0];

        eventSource = EmitEvents(rnd, 1, 0, txLoader.GetAbi());
        events = GetFromEventsSource(eventSource, 5);
    
        txs = OnNewLogs(rnd, chain, txLoader, events.Logs, new() {
            Slot = _initialSlot + 1,
            Eon = 1,
            TxPointer = 0,
            Keys = events.Keys
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(5));
    }

    // todo: test transactions with overlapping eons

    private ShutterTransactions OnNewLogs(Random rnd, MergeTestBlockchain chain, ShutterTxLoader txLoader, in LogEntry[] logs, IShutterMessageHandler.ValidatedKeyArgs keys)
    {
        Block head = chain.BlockTree.Head!;
        BlockHeader parentHeader = chain.BlockTree.FindParentHeader(head.Header, Blockchain.BlockTreeLookupOptions.None)!;

        if (logs.Length > 0)
        {
            TxReceipt[] receipts = InsertShutterReceipts(rnd, chain.ReceiptStorage, head, logs);
            txLoader.LoadFromReceipts(head, receipts);
        }

        return txLoader.LoadTransactions(head, parentHeader, keys);
    }

    private (LogEntry[], List<(byte[] IdentityPreimage, byte[] Key)>) GetFromEventsSource(IEnumerable<ShutterEventEmitter.Event> eventSource, int count)
    {
        List<ShutterEventEmitter.Event> events = eventSource.Take(count).ToList();
        LogEntry[] logs = events.Select(e => e.LogEntry).ToArray();
        List<(byte[] IdentityPreimage, byte[] Key)> keys = events.Select(e => (e.IdentityPreimage, e.Key)).ToList();
        keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
        return (logs, keys);
    }

    private ShutterTxLoader InitTxLoader(ILogFinder logFinder)
        => new(
            logFinder,
            _cfg,
            ChiadoSpecProvider.Instance,
            _ecdsa,
            LimboLogs.Instance
        );

    private IEnumerable<ShutterEventEmitter.Event> EmitEvents(Random rnd, ulong eon, ulong initialTxPointer, AbiEncodingInfo abi)
        => new ShutterEventEmitter(
            rnd,
            BlockchainIds.Chiado,
            eon,
            initialTxPointer,
            _abiEncoder,
            new(_cfg.SequencerContractAddress!),
            abi
        ).EmitEvents();

    private IEnumerable<ShutterEventEmitter.Event> EmitHalfInvalidEvents(Random rnd, ulong initialTxPointer, AbiEncodingInfo abi)
    {
        ShutterEventEmitter emitter = new(
            rnd,
            BlockchainIds.Chiado,
            0,
            initialTxPointer,
            _abiEncoder,
            new(_cfg.SequencerContractAddress!),
            abi
        );

        IEnumerable<Transaction> emitHalfInvalid()
        {
            bool valid = false;
            while (true)
            {
                valid = !valid;
                yield return valid
                    ? emitter.DefaultTx
                    : Build.A.Transaction.TestObject;
            }
        }

        return emitter.EmitEvents(emitter.EmitDefaultGasLimits(), emitHalfInvalid());
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
}

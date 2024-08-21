// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Blockchain.Find;
using System.Linq;
using Nethermind.Specs.Forks;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
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
    [Test]
    public async Task Can_load_transactions_over_slots()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ulong slot = ShutterTestsCommon.InitialSlot;
        ulong txPointer = ShutterTestsCommon.InitialTxPointer;

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterTxLoader txLoader = ShutterTestsCommon.InitApi(chain).TxLoader;
        IEnumerable<ShutterEventEmitter.Event> eventSource = ShutterTestsCommon.EmitEvents(rnd, 0, txPointer, txLoader.GetAbi());

        for (int i = 0; i < 1; i++)
        {
            (LogEntry[] Logs, List<(byte[] IdentityPreimage, byte[] Key)> Keys) events = ShutterTestsCommon.GetFromEventsSource(eventSource, 20);
        
            ShutterTransactions txs = InsertAndLoadLogs(rnd, chain, txLoader, events.Logs, new() {
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
        Random rnd = new(ShutterTestsCommon.Seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterTxLoader txLoader = ShutterTestsCommon.InitApi(chain).TxLoader;
        IEnumerable<ShutterEventEmitter.Event> eventSource = ShutterTestsCommon.EmitHalfInvalidEvents(rnd, ShutterTestsCommon.InitialTxPointer, txLoader.GetAbi());

        (LogEntry[] Logs, List<(byte[] IdentityPreimage, byte[] Key)> Keys) events = ShutterTestsCommon.GetFromEventsSource(eventSource, 20);
    
        Block head = chain.BlockTree.Head!;
        BlockHeader parentHeader = chain.BlockTree.FindParentHeader(head.Header, Blockchain.BlockTreeLookupOptions.None)!;
        TxReceipt[] receipts = InsertShutterReceipts(rnd, chain.ReceiptStorage, head, events.Logs);
        txLoader.LoadFromReceipts(head, receipts);

        ShutterTransactions txs = InsertAndLoadLogs(rnd, chain, txLoader, events.Logs, new() {
            Slot = ShutterTestsCommon.InitialSlot,
            Eon = 0,
            TxPointer = ShutterTestsCommon.InitialTxPointer,
            Keys = events.Keys
        });

        // half of transactions were invalid, should have been filtered
        Assert.That(txs.Transactions, Has.Length.EqualTo(10));
    }

    [Test]
    public async Task Can_load_up_to_gas_limit()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterTxLoader txLoader = ShutterTestsCommon.InitApi(chain).TxLoader;
        IEnumerable<ShutterEventEmitter.Event> eventSource = ShutterTestsCommon.EmitEvents(rnd, 0, ShutterTestsCommon.InitialTxPointer, txLoader.GetAbi());

        List<ShutterEventEmitter.Event> events = eventSource.Take(40).ToList();
        LogEntry[] logs = events.Select(e => e.LogEntry).ToArray();

        List<(byte[] IdentityPreimage, byte[] Key)> keys = events.Select(e => (e.IdentityPreimage, e.Key)).ToList();

        List<(byte[] IdentityPreimage, byte[] Key)> slot1Keys = keys[..20];
        slot1Keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));

        List<(byte[] IdentityPreimage, byte[] Key)> slot2Keys = keys[20..];
        slot2Keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
    
        ShutterTransactions txs = InsertAndLoadLogs(rnd, chain, txLoader, logs, new() {
            Slot = ShutterTestsCommon.InitialSlot,
            Eon = 0,
            TxPointer = ShutterTestsCommon.InitialTxPointer,
            Keys = slot1Keys
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(20));

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
        lastPayload = payloads[0];

        txs = InsertAndLoadLogs(rnd, chain, txLoader, [], new() {
            Slot = ShutterTestsCommon.InitialSlot + 1,
            Eon = 0,
            TxPointer = ShutterTestsCommon.InitialTxPointer + 20,
            Keys = slot2Keys
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(20));

        payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
        lastPayload = payloads[0];

        txs = InsertAndLoadLogs(rnd, chain, txLoader, [], new() {
            Slot = ShutterTestsCommon.InitialSlot + 2,
            Eon = 0,
            TxPointer = ShutterTestsCommon.InitialTxPointer + 40,
            Keys = []
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(0));
    }

    [Test]
    public async Task Can_load_transactions_over_eons()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterTxLoader txLoader = ShutterTestsCommon.InitApi(chain).TxLoader;

        IEnumerable<ShutterEventEmitter.Event> eventSource = ShutterTestsCommon.EmitEvents(rnd, 0, ShutterTestsCommon.InitialTxPointer, txLoader.GetAbi());
        (LogEntry[] Logs, List<(byte[] IdentityPreimage, byte[] Key)> Keys) events = ShutterTestsCommon.GetFromEventsSource(eventSource, 5);
    
        ShutterTransactions txs = InsertAndLoadLogs(rnd, chain, txLoader, events.Logs, new() {
            Slot = ShutterTestsCommon.InitialSlot,
            Eon = 0,
            TxPointer = ShutterTestsCommon.InitialTxPointer,
            Keys = events.Keys
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(5));

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true);
        lastPayload = payloads[0];

        eventSource = ShutterTestsCommon.EmitEvents(rnd, 1, 0, txLoader.GetAbi());
        events = ShutterTestsCommon.GetFromEventsSource(eventSource, 5);
    
        txs = InsertAndLoadLogs(rnd, chain, txLoader, events.Logs, new() {
            Slot = ShutterTestsCommon.InitialSlot + 1,
            Eon = 1,
            TxPointer = 0,
            Keys = events.Keys
        });

        Assert.That(txs.Transactions, Has.Length.EqualTo(5));
    }

    // todo: test transactions with overlapping eons

    private ShutterTransactions InsertAndLoadLogs(Random rnd, MergeTestBlockchain chain, ShutterTxLoader txLoader, in LogEntry[] logs, IShutterKeyValidator.ValidatedKeyArgs keys)
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

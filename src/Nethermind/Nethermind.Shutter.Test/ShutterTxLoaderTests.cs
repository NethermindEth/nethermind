// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Blockchain.Find;
using System.Threading.Tasks;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Blockchain.Receipts;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Core.Specs;
using Nethermind.State;
using Nethermind.Shutter.Config;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Shutter.Test;

using static Nethermind.Merge.AuRa.Test.AuRaMergeEngineModuleTests;

[TestFixture]
class ShutterTxLoaderTests : EngineModuleTests
{
    private class ShutterApiSimulatorLoadedTxs(IAbiEncoder abiEncoder, IReadOnlyBlockTree blockTree, IEthereumEcdsa ecdsa, ILogFinder logFinder, IReceiptStorage receiptStorage, ILogManager logManager, ISpecProvider specProvider, ITimestamper timestamper, IWorldStateManager worldStateManager, IShutterConfig cfg, Dictionary<ulong, byte[]> validatorsInfo, Random rnd) : ShutterApiSimulator(abiEncoder, blockTree, ecdsa, logFinder, receiptStorage, logManager, specProvider, timestamper, worldStateManager, cfg, validatorsInfo, rnd)
    {
        public ShutterTransactions? LoadedTransactions;
        // instead of loading to TxSouce store to check result
        protected override void KeysValidatedHandler(object? sender, IShutterKeyValidator.ValidatedKeyArgs keys)
        {
            Block head = _blockTree.Head!;
            BlockHeader parentHeader = _blockTree.FindParentHeader(head.Header, BlockTreeLookupOptions.None)!;
            LoadedTransactions = TxLoader.LoadTransactions(head, parentHeader, keys);
        }
    }

    private class ShutterEventSimulatorHalfInvalid(Random rnd, ulong chainId, ulong eon, ulong threshold, ulong slot, ulong txIndex, IAbiEncoder abiEncoder, Address sequencerContractAddress) : ShutterEventSimulator(rnd, chainId, eon, threshold, slot, txIndex, abiEncoder, sequencerContractAddress)
    {
        private readonly Transaction _validTx = Build.A.Transaction.WithChainId(chainId).Signed().TestObject;
        private readonly Transaction _invalidTx = Build.A.Transaction.TestObject;
        protected override IEnumerable<Event> EmitEvents()
        {
            IEnumerable<Transaction> emitHalfInvalid()
            {
                bool valid = false;
                while (true)
                {
                    valid = !valid;
                    yield return valid ? _validTx : _invalidTx;
                }
            }

            return EmitEvents(EmitDefaultGasLimits(), emitHalfInvalid());
        }
    }

    [Test]
    public async Task Can_load_transactions_over_slots()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterApiSimulatorLoadedTxs api = InitApi(rnd, chain);
        api.SetEventSimulator(ShutterTestsCommon.InitEventSimulator(rnd, 0, 10, ShutterTestsCommon.InitialTxPointer));

        for (int i = 0; i < 20; i++)
        {
            api.AdvanceSlot(20);

            Assert.That(api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(20));

            IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
            lastPayload = payloads[0];
        }
    }

    [Test]
    public async Task Can_load_and_filter_transactions()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterApiSimulatorLoadedTxs api = InitApi(rnd, chain);
        api.SetEventSimulator(new ShutterEventSimulatorHalfInvalid(
            rnd,
            ShutterTestsCommon.ChainId,
            0,
            10,
            ShutterTestsCommon.InitialSlot,
            ShutterTestsCommon.InitialTxPointer,
            ShutterTestsCommon.AbiEncoder,
            new(ShutterTestsCommon.Cfg.SequencerContractAddress!)
        ));

        api.AdvanceSlot(20);

        // half of transactions were invalid, should have been filtered
        Assert.That(api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(10));
    }

    [Test]
    public async Task Can_load_up_to_gas_limit()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterApiSimulatorLoadedTxs api = InitApi(rnd, chain);
        api.SetEventSimulator(ShutterTestsCommon.InitEventSimulator(rnd, 0, 10, ShutterTestsCommon.InitialTxPointer));

        api.AdvanceSlot(40);

        Assert.Multiple(() =>
        {
            Assert.That(api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot));
            Assert.That(api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(20));
        });


        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        api.AdvanceSlot(0);

        Assert.Multiple(() =>
        {
            Assert.That(api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot + 1));
            Assert.That(api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(20));
        });


        payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        api.AdvanceSlot(0);

        Assert.Multiple(() =>
        {
            Assert.That(api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot + 2));
            Assert.That(api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(0));
        });

    }

    [Test]
    public async Task Can_load_transactions_over_eons()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterApiSimulatorLoadedTxs api = InitApi(rnd, chain);
        api.SetEventSimulator(ShutterTestsCommon.InitEventSimulator(rnd, 0, 10, ShutterTestsCommon.InitialTxPointer));

        api.AdvanceSlot(5);

        Assert.Multiple(() =>
        {
            Assert.That(api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot));
            Assert.That(api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(5));
        });


        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        api.NewEon();
        api.AdvanceSlot(5);

        Assert.Multiple(() =>
        {
            Assert.That(api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot + 1));
            Assert.That(api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(5));
        });

    }

    [Test]
    public async Task Can_scan_logs_to_genesis()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterApiSimulatorLoadedTxs api = InitApi(rnd, chain);
        api.SetEventSimulator(ShutterTestsCommon.InitEventSimulator(rnd, 0, 10, ShutterTestsCommon.InitialTxPointer));

        Assert.DoesNotThrow(() => api.AdvanceSlot(0));
    }

    // todo: test transactions with overlapping eons

    private static ShutterApiSimulatorLoadedTxs InitApi(Random rnd, MergeTestBlockchain chain)
        => new(
            ShutterTestsCommon.AbiEncoder, chain.BlockTree.AsReadOnly(), chain.EthereumEcdsa, chain.LogFinder, chain.ReceiptStorage,
            chain.LogManager, chain.SpecProvider, chain.Timestamper, chain.WorldStateManager, ShutterTestsCommon.Cfg, [], rnd
        );
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using NUnit.Framework;
using System.Threading.Tasks;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Abi;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Test;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterTxLoaderTests : BaseEngineModuleTests
{
    private class ShutterEventSimulatorHalfInvalid(Random rnd, ulong chainId, ulong threshold, ulong slot, IAbiEncoder abiEncoder, Address sequencerContractAddress) : ShutterEventSimulator(rnd, chainId, threshold, slot, abiEncoder, sequencerContractAddress)
    {
        private readonly Transaction _validTx = Build.A.Transaction.WithChainId(chainId).Signed().TestObject;
        private readonly Transaction _invalidTx = Build.A.Transaction.TestObject;
        protected override IEnumerable<Event> EmitEvents()
        {
            IEnumerable<Transaction> EmitHalfInvalid()
            {
                bool valid = false;
                while (true)
                {
                    valid = !valid;
                    yield return valid ? _validTx : _invalidTx;
                }
            }

            return EmitEvents(EmitDefaultEons(), EmitHalfInvalid());
        }
    }

    private class ShutterEventSimulatorHalfNextEon(Random rnd, ulong chainId, ulong threshold, ulong slot, IAbiEncoder abiEncoder, Address sequencerContractAddress) : ShutterEventSimulator(rnd, chainId, threshold, slot, abiEncoder, sequencerContractAddress)
    {
        protected override IEnumerable<Event> EmitEvents()
        {
            IEnumerable<ulong> EmitHalfNextEon()
            {
                bool next = false;
                while (true)
                {
                    next = !next;
                    yield return next ? _eon + 1 : _eon;
                }
            }

            return EmitEvents(EmitHalfNextEon(), EmitDefaultTransactions());
        }
    }

    [Test]
    public async Task Can_load_transactions_over_slots()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        for (int i = 0; i < 20; i++)
        {
            chain.Api!.AdvanceSlot(20);

            Assert.That(chain.Api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(20));

            IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
            lastPayload = payloads[0];
        }
    }

    [Test]
    public async Task Can_load_and_filter_transactions()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ShutterEventSimulatorHalfNextEon eventSimulator = new(
            rnd,
            ShutterTestsCommon.ChainId,
            ShutterTestsCommon.Threshold,
            ShutterTestsCommon.InitialSlot,
            ShutterTestsCommon.AbiEncoder,
            new(ShutterTestsCommon.Cfg.SequencerContractAddress!)
        );

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd, null, eventSimulator).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        chain.Api!.AdvanceSlot(20);

        // half of transactions were invalid, should have been filtered
        Assert.That(chain.Api.LoadedTransactions!.Value.Transactions, Has.Length.EqualTo(10));
    }

    [Test]
    public async Task Can_load_up_to_gas_limit()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        chain.Api!.AdvanceSlot(40);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot));
            Assert.That(chain.Api.LoadedTransactions.Value.Transactions, Has.Length.EqualTo(20));
        });


        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        chain.Api.AdvanceSlot(0);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot + 1));
            Assert.That(chain.Api.LoadedTransactions.Value.Transactions, Has.Length.EqualTo(20));
        });


        payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        chain.Api.AdvanceSlot(0);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot + 2));
            Assert.That(chain.Api.LoadedTransactions.Value.Transactions, Has.Length.EqualTo(0));
        });

    }

    [Test]
    public async Task Can_load_transactions_over_eons()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        chain.Api!.AdvanceSlot(5);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot));
            Assert.That(chain.Api.LoadedTransactions.Value.Transactions, Has.Length.EqualTo(5));
        });


        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        chain.Api.NextEon();
        chain.Api.AdvanceSlot(5);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot + 1));
            Assert.That(chain.Api.LoadedTransactions.Value.Transactions, Has.Length.EqualTo(5));
        });
    }

    [Test]
    public async Task Can_scan_logs_to_genesis()
    {
        Random rnd = new(ShutterTestsCommon.Seed);

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        Assert.DoesNotThrow(() => chain.Api!.AdvanceSlot(0));
    }

    [Test]
    public async Task Can_load_transactions_with_overlapping_eons()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ShutterEventSimulatorHalfNextEon eventSimulator = new(
            rnd,
            ShutterTestsCommon.ChainId,
            ShutterTestsCommon.Threshold,
            ShutterTestsCommon.InitialSlot,
            ShutterTestsCommon.AbiEncoder,
            new(ShutterTestsCommon.Cfg.SequencerContractAddress!)
        );

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd, null, eventSimulator).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        chain.Api!.AdvanceSlot(20);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot));
            Assert.That(chain.Api.LoadedTransactions.Value.Transactions, Has.Length.EqualTo(10));
        });

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        chain.Api.NextEon();
        chain.Api.AdvanceSlot(0);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Api.LoadedTransactions!.Value.Slot, Is.EqualTo(ShutterTestsCommon.InitialSlot + 1));
            Assert.That(chain.Api.LoadedTransactions.Value.Transactions, Has.Length.EqualTo(10));
        });
    }
}

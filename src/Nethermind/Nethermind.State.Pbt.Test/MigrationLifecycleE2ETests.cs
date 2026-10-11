// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class MigrationLifecycleE2ETests
{
    [Test]
    public async Task Reference_transactions_cross_activation_and_recross_in_one_branch([Values] bool portable, [Values] FlatLayout layout)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        await using MigrationLifecycleHarness harness = await MigrationLifecycleHarness.Create(Path.Combine(scratch.Path, "target"), portable, layout);
        IContainer container = harness.Container;
        Dictionary<string, Block> blocks = harness.Blocks;
        Dictionary<string, JsonElement> expected = harness.Expected;
        IBlockTree tree = harness.Tree;
        IWorldStateManager manager = container.Resolve<IWorldStateManager>();
        IMigrationTelemetry telemetry = harness.Telemetry;
        Assert.That(telemetry.GetShadowRoot(blocks["anchor"].Hash!), Is.EqualTo(harness.PbtRoot("anchor")));
        IMainProcessingContext processing = container.Resolve<IMainProcessingContext>();
        foreach (string name in blocks.Keys)
            if (name != "anchor") harness.Prepare(name);
        Stopwatch elapsed = Stopwatch.StartNew();
        List<int> branchSizes = [];
        processing.BranchProcessor.BlocksProcessing += (_, args) => branchSizes.Add(args.Blocks.Count);
        ProcessingOptions options = ProcessingOptions.MarkAsProcessed | ProcessingOptions.DoNotUpdateHead | ProcessingOptions.StoreReceipts;
        ReplayIntoPbt("a3");
        Assert.That(processing.BlockchainProcessor.Process(blocks["a5"], options, NullBlockTracer.Instance)?.Hash, Is.EqualTo(blocks["a5"].Hash));
        Assert.That(branchSizes, Is.EqualTo(new[] { 5 }), "one processing request must span activation");
        foreach (string name in new[] { "a1", "a2", "a3", "a4", "a5" }) AssertRoot(name);
        await using (ScopedBlockProducerEnv producer = container.Resolve<IBlockProducerEnvFactory>().CreateTransient())
        {
            Block candidateInput = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(expected["a4"].GetProperty("blockRlp").GetString()!)))!;
            candidateInput.Header.IsPostMerge = true;
            candidateInput.Header.TotalDifficulty = 0;
            foreach (IBlockPreprocessorStep preprocessor in container.Resolve<IReadOnlyList<IBlockPreprocessorStep>>()) preprocessor.RecoverData(candidateInput);
            Block? candidate = producer.ChainProcessor.Process(candidateInput,
                ProcessingOptions.ProducingBlock | ProcessingOptions.IgnoreParentNotOnMainChain, NullBlockTracer.Instance);
            Assert.That(candidate, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(candidate!.StateRoot, Is.EqualTo(blocks["a4"].StateRoot), "private producer computes independent first-PBT-child root");
                Assert.That(candidate.Transactions.Length, Is.EqualTo(blocks["a4"].Transactions.Length));
            }
        }
        AssertRoot("a3");
        AssertRoot("a5");
        Select("a5", Hash256.Zero);
        Assert.That(telemetry.GetProgress().Phase, Is.EqualTo("running"));
        AssertTransition("a3", "a4", "a5");
        Select("a1", Hash256.Zero);
        ReplayIntoPbt("b3");
        Assert.That(processing.BlockchainProcessor.Process(blocks["b6"], options, NullBlockTracer.Instance)?.Hash, Is.EqualTo(blocks["b6"].Hash));
        Assert.That(branchSizes, Is.EqualTo(new[] { 5, 5 }));
        foreach (string name in new[] { "b2", "b3", "b4", "b5", "b6" }) AssertRoot(name);
        Select("b6", blocks["a1"].Hash!);
        Assert.That(telemetry.GetProgress().Phase, Is.EqualTo("running"));
        AssertTransition("b3", "b4", "b5", "b6");
        Select("b6", blocks["b4"].Hash!);
        Assert.That(telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc("done", null, null)));
        TestContext.Out.WriteLine($"Executed 10 independent geth blocks in {elapsed.Elapsed.TotalSeconds:F3}s; source={(portable ? "portable" : "preimage genesis")}; geth=e31a37fb88c2b75c0897c033bd3f4279dee42268");
        IPersistence flatPersistence = container.Resolve<IPersistence>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IFlatDbConfig>().Layout, Is.EqualTo(layout));
            // Flat gets no commits after activation; finalizing the activation persists it up to the activation parent.
            Assert.That(() => FlatState(flatPersistence), Is.EqualTo(new StateId(blocks["b3"].Header)).After(10_000, 50));
        }

        // A request spanning activation needs PBT at the activation parent before main processing reaches it; the branch
        // follower replays that non-canonical, not yet processed branch from its stored BALs.
        void ReplayIntoPbt(string activationParent)
        {
            container.Resolve<PbtBranchFollower>().Add(blocks[activationParent].Header);
            harness.WaitForPbt(blocks[activationParent].Header);
        }

        void Select(string name, Hash256 finalized)
        {
            Assert.That(tree.TryUpdateMainChain(blocks[name].Header, true, true, [blocks[name]]), Is.True);
            tree.ForkChoiceUpdated(finalized, Hash256.Zero);
        }

        // The Merkle shadow is replayed asynchronously once the branch is canonical, so it is asserted after the selection.
        void AssertTransition(string activationParent, params string[] window)
        {
            foreach (string name in window)
                Assert.That(() => telemetry.GetShadowRoot(blocks[name].Hash!), Is.EqualTo(harness.ExpectedShadowRoot(name)).After(10_000, 50), name);
            MigrationProgressForRpc progress = telemetry.GetProgress();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(progress.Binary, Is.EqualTo(new MigrationDirectionForRpc("parked", blocks[activationParent].Number, blocks[activationParent].Hash!,
                    harness.PbtRoot(activationParent), "")));
                Assert.That(progress.Merkle, Is.EqualTo(new MigrationDirectionForRpc("synced", blocks[window[^1]].Number, blocks[window[^1]].Hash!,
                    harness.ExpectedShadowRoot(window[^1]), "")));
            }
        }

        void AssertRoot(string name)
        {
            if (!expected[name].GetProperty("binary").GetBoolean())
                Assert.That(telemetry.GetShadowRoot(blocks[name].Hash!), Is.EqualTo(harness.ExpectedShadowRoot(name)), name);
            Assert.That(harness.Reader.HasStateForBlock(blocks[name].Header), Is.True, name);
            harness.AssertAllocation(name);
            Dictionary<Address, GethGenesisAllocJson> allocation = Eip8347FixtureState.LoadAllocation(harness.FixtureDirectory, name);
            IStateReader reader = manager.GlobalStateReader;
            // Probe keys on the losing branch even when their zero values are omitted from the reference allocation.
            Address writer = new("0x1000000000000000000000000000000000000001");
            foreach (UInt256 slot in new UInt256[] { 0, 63, 64, 255, 256 })
            {
                UInt256 expectedValue = allocation.GetValueOrDefault(writer)?.Storage?.GetValueOrDefault(slot) is { } value ? new UInt256(value, isBigEndian: true) : 0;
                Assert.That(reader.GetStorage(blocks[name].Header, writer, slot), Is.EqualTo(expectedValue), $"{name} restored slot {slot}");
            }
        }
    }

    [Test]
    public async Task Created_and_selfdestructed_account_is_absent_from_both_commitments([Values] bool afterActivation)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        await using MigrationLifecycleHarness harness = await MigrationLifecycleHarness.Create(Path.Combine(scratch.Path, "target"), portable: true, FlatLayout.Flat);
        await harness.Scheduler.DisposeAsync();
        IMainProcessingContext processing = harness.Container.Resolve<IMainProcessingContext>();
        Block parent = harness.Anchor;
        if (afterActivation)
        {
            harness.ProcessBranch(["a1", "a2", "a3", "a4"], expectPbt: false);
            parent = harness.Blocks["a4"];
        }
        using PrivateKey sender = new("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        IStateReader reader = harness.Container.Resolve<IWorldStateManager>().GlobalStateReader;
        ulong nonce = reader.GetNonce(parent.Header, sender.Address);
        Address destroyed = ContractAddress.From(sender.Address, nonce);
        byte[] initCode = Bytes.FromHexString("0x73" + sender.Address.ToString()[2..] + "ff");
        Transaction transaction = Build.A.Transaction.WithChainId(1337).WithNonce(nonce).WithTo(null)
            .WithGasLimit(1_000_000).WithGasPrice(2_000_000_000).WithData(initCode).SignedAndResolved(sender).TestObject;
        BlockHeader template = harness.Blocks[afterActivation ? "a5" : "a1"].Header.Clone();
        template.IsPostMerge = true;
        template.TotalDifficulty = 0;
        Block proposed = new(template, [transaction], [], []);
        Block produced;
        await using (ScopedBlockProducerEnv producer = harness.Container.Resolve<IBlockProducerEnvFactory>().CreateTransient())
        {
            produced = producer.ChainProcessor.Process(proposed, ProcessingOptions.ProducingBlock | ProcessingOptions.IgnoreParentNotOnMainChain,
                NullBlockTracer.Instance)!;
            Assert.That(produced, Is.Not.Null);
            Assert.That(produced.Transactions, Has.Length.EqualTo(1));
        }
        Prepare(produced);
        Assert.That(processing.BlockchainProcessor.Process(produced, ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts,
            NullBlockTracer.Instance)?.Hash, Is.EqualTo(produced.Hash));
        TxReceipt[] receipts = harness.Container.Resolve<IReceiptStorage>().Get(produced);
        Assert.That(receipts, Has.Length.EqualTo(1));
        Assert.That(receipts[0].StatusCode, Is.EqualTo(StatusCode.Success), "absence must follow successful SELFDESTRUCT, not failed creation");
        harness.Tree.TryUpdateMainChain(produced.Header, true, true, [produced]);
        Assert.That(await harness.Container.Resolve<PbtBalFollower>().Follow(produced.Header, CancellationToken.None), Is.True);
        List<IStateReader> commitments = [harness.Container.Resolve<PbtStateReader>()];
        if (!afterActivation) commitments.Add(harness.Container.Resolve<FlatStateReader>());
        foreach (IStateReader commitment in commitments)
        {
            Assert.That(commitment.HasStateForBlock(produced.Header), Is.True, commitment.GetType().Name);
            Assert.That(commitment.TryGetAccount(produced.Header, destroyed, out _), Is.False, $"{commitment.GetType().Name}: destroyed creation must not survive block commit");
        }

        void Prepare(Block block)
        {
            block.Header.IsPostMerge = true;
            if (block.EncodedBlockAccessList is { } encoded)
                block.BlockAccessList = Rlp.Decode<Nethermind.Core.BlockAccessLists.ReadOnlyBlockAccessList>(encoded);
            foreach (IBlockPreprocessorStep preprocessor in harness.Container.Resolve<IReadOnlyList<IBlockPreprocessorStep>>()) preprocessor.RecoverData(block);
            harness.Tree.SuggestBlock(block);
            harness.Container.Resolve<IBlockAccessListStore>().InsertFromBlock(block);
        }
    }

    private static StateId FlatState(IPersistence persistence)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        return reader.CurrentState;
    }
}

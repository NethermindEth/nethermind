// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Core.Specs;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.State;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class MigrationLifecycleE2ETests
{
    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");

    [Test]
    public async Task Delayed_follower_authenticates_bal_and_recovers_from_gap([Values] bool portable, [Values] bool corrupt)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        await using MigrationLifecycleHarness harness = await MigrationLifecycleHarness.Create(Path.Combine(scratch.Path, "target"), portable);
        await harness.Scheduler.DisposeAsync();
        IBlockAccessListStore store = harness.Container.Resolve<IBlockAccessListStore>();
        // Simulate a separately validated MPT chain ahead of the follower without executing these BAL-only fixtures.
        foreach (string name in new[] { "a1", "a2", "a3" })
        {
            Block block = harness.Blocks[name];
            harness.Tree.SuggestBlock(block);
            harness.Tree.TryUpdateMainChain(block.Header, true, true, [block]);
            if (name != "a2") store.Insert(block.Number, block.Hash!, Bytes.FromHexString(harness.Expected[name].GetProperty("balRlp").GetString()!));
        }
        Block missing = harness.Blocks["a2"];
        if (corrupt) store.Insert(missing.Number, missing.Hash!, Bytes.FromHexString("0xc0"));
        PbtBalFollower follower = harness.Container.Resolve<PbtBalFollower>();
        Assert.That(await follower.Follow(harness.Blocks["a3"].Header, CancellationToken.None), Is.False);
        AssertPbtState(harness, "a1");
        Assert.That(harness.Pbt.HasStateForBlock(new StateId(harness.Blocks["a2"].Header)), Is.False);
        Assert.That(follower.Error, Is.Not.Empty);
        store.Insert(missing.Number, missing.Hash!, Bytes.FromHexString(harness.Expected["a2"].GetProperty("balRlp").GetString()!));
        Stopwatch elapsed = Stopwatch.StartNew();
        Assert.That(await follower.Follow(harness.Blocks["a3"].Header, CancellationToken.None), Is.True);
        elapsed.Stop();
        using (Assert.EnterMultipleScope())
        {
            AssertPbtState(harness, "a3");
            Assert.That(follower.Cursor!.Hash, Is.EqualTo(harness.Blocks["a3"].Hash));
            Assert.That(follower.Error, Is.Null);
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(24)), "two replayed blocks must beat the fixture's 12-second producer interval");
        }
        TestContext.Out.WriteLine($"Replayed two authenticated reference BALs in {elapsed.Elapsed.TotalMilliseconds:F1}ms without EVM; source portable={portable}, corrupted={corrupt}");
    }

    [Test]
    public async Task Reference_transactions_cross_activation_and_recross_in_one_branch([Values] bool portable)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        await using MigrationLifecycleHarness harness = await MigrationLifecycleHarness.Create(Path.Combine(scratch.Path, "target"), portable,
            fixtureDirectory: Path.Combine(Fixtures, "builder-predeploys"));
        IContainer container = harness.Container;
        Dictionary<string, Block> blocks = harness.Blocks;
        Dictionary<string, JsonElement> expected = harness.Expected;
        IBlockTree tree = harness.Tree;
        IWorldStateManager manager = container.Resolve<IWorldStateManager>();
        IMigrationTelemetry telemetry = harness.Telemetry;
        Assert.That(telemetry.GetShadowRoot(blocks["anchor"].Hash!), Is.EqualTo(new Hash256(expected["anchor"].GetProperty("pbtRoot").GetString()!)));
        IMainProcessingContext processing = container.Resolve<IMainProcessingContext>();
        IBlockAccessListStore bals = container.Resolve<IBlockAccessListStore>();
        foreach ((string name, Block block) in blocks)
        {
            if (name == "anchor") continue;
            byte[] balBytes = Bytes.FromHexString(expected[name].GetProperty("balRlp").GetString()!);
            bals.Insert(block.Number, block.Hash!, balBytes);
            RlpReader balReader = new(balBytes);
            block.BlockAccessList = BlockAccessListDecoder.Instance.Decode(ref balReader);
            block.Header.IsPostMerge = true;
            foreach (IBlockPreprocessorStep preprocessor in container.Resolve<IReadOnlyList<IBlockPreprocessorStep>>()) preprocessor.RecoverData(block);
            tree.SuggestBlock(block);
        }
        Stopwatch elapsed = Stopwatch.StartNew();
        List<int> branchSizes = [];
        processing.BranchProcessor.BlocksProcessing += (_, args) => branchSizes.Add(args.Blocks.Count);
        ProcessingOptions options = ProcessingOptions.MarkAsProcessed | ProcessingOptions.DoNotUpdateHead | ProcessingOptions.StoreReceipts;
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
        Select("a1", Hash256.Zero);
        Assert.That(processing.BlockchainProcessor.Process(blocks["b6"], options, NullBlockTracer.Instance)?.Hash, Is.EqualTo(blocks["b6"].Hash));
        Assert.That(branchSizes, Is.EqualTo(new[] { 5, 5 }));
        foreach (string name in new[] { "b2", "b3", "b4", "b5", "b6" }) AssertRoot(name);
        Select("b6", blocks["a1"].Hash!);
        Assert.That(telemetry.GetProgress().Phase, Is.EqualTo("running"));
        Select("b6", blocks["b4"].Hash!);
        Assert.That(telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc("done", null, null)));
        TestContext.Out.WriteLine($"Executed 10 independent geth blocks in {elapsed.Elapsed.TotalSeconds:F3}s; source={(portable ? "portable" : "preimage genesis")}; geth=e31a37fb88c2b75c0897c033bd3f4279dee42268");
        IPersistence flatPersistence = container.Resolve<IPersistence>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IFlatDbConfig>().Layout, Is.EqualTo(FlatLayout.Flat));
            // Flat gets no commits after activation; finalizing the activation persists it up to the activation parent.
            Assert.That(() => FlatState(flatPersistence), Is.EqualTo(new Flat.StateId(blocks["b3"].Header)).After(10_000, 50));
        }

        void Select(string name, Hash256 finalized)
        {
            Assert.That(tree.TryUpdateMainChain(blocks[name].Header, true, true, [blocks[name]]), Is.True);
            tree.ForkChoiceUpdated(finalized, Hash256.Zero);
        }

        void AssertRoot(string name)
        {
            Assert.That(telemetry.GetShadowRoot(blocks[name].Hash!), Is.EqualTo(ExpectedShadowRoot(harness, name)), name);
            Assert.That(harness.Reader.HasStateForBlock(blocks[name].Header), Is.True, name);
            using JsonDocument allocation = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(harness.FixtureDirectory, "states", name + ".alloc.json")));
            IStateReader reader = manager.GlobalStateReader;
            foreach (JsonProperty account in allocation.RootElement.EnumerateObject())
            {
                Address address = new(account.Name);
                JsonElement value = account.Value;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(reader.GetBalance(blocks[name].Header, address), Is.EqualTo(Parse(value.GetProperty("balance").GetString()!)), $"{name} balance {address}");
                    Assert.That(reader.GetNonce(blocks[name].Header, address), Is.EqualTo(value.TryGetProperty("nonce", out JsonElement nonce) ? (ulong)Parse(nonce.GetString()!) : 0UL), $"{name} nonce {address}");
                    Assert.That(reader.GetCode(blocks[name].Header, address), Is.EqualTo(value.TryGetProperty("code", out JsonElement code) ? Bytes.FromHexString(code.GetString()!) : []), $"{name} code {address}");
                }
                if (value.TryGetProperty("storage", out JsonElement storage))
                    foreach (JsonProperty slot in storage.EnumerateObject())
                        Assert.That(reader.GetStorage(blocks[name].Header, address, Parse(slot.Name)), Is.EqualTo(Parse(slot.Value.GetString()!)), $"{name} slot {address}/{slot.Name}");
            }
            // Probe keys on the losing branch even when their zero values are omitted from the reference allocation.
            Address writer = new("0x1000000000000000000000000000000000000001");
            foreach (UInt256 slot in new UInt256[] { 0, 63, 64, 255, 256 })
            {
                UInt256 expectedValue = 0;
                if (allocation.RootElement.TryGetProperty(writer.ToString(), out JsonElement account) && account.TryGetProperty("storage", out JsonElement storage))
                    foreach (JsonProperty entry in storage.EnumerateObject()) if (Parse(entry.Name) == slot) expectedValue = Parse(entry.Value.GetString()!);
                Assert.That(reader.GetStorage(blocks[name].Header, writer, slot), Is.EqualTo(expectedValue), $"{name} restored slot {slot}");
            }
        }
    }

    [Test]
    public async Task Created_and_selfdestructed_account_is_absent_from_both_commitments([Values] bool portable, [Values] bool afterActivation)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        await using MigrationLifecycleHarness harness = await MigrationLifecycleHarness.Create(Path.Combine(scratch.Path, "target"), portable,
            fixtureDirectory: Path.Combine(Fixtures, "builder-predeploys"));
        await harness.Scheduler.DisposeAsync();
        IMainProcessingContext processing = harness.Container.Resolve<IMainProcessingContext>();
        Block parent = harness.Anchor;
        if (afterActivation)
        {
            for (int number = 1; number <= 4; number++)
            {
                Block block = harness.Blocks[$"a{number}"];
                Prepare(block);
                Assert.That(processing.BlockchainProcessor.Process(block, ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts,
                    NullBlockTracer.Instance)?.Hash, Is.EqualTo(block.Hash));
                parent = block;
            }
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
            if (block.EncodedBlockAccessList is null && block.GeneratedBlockAccessList is null)
                block.EncodedBlockAccessList = Bytes.FromHexString(harness.Expected[$"a{block.Number}"].GetProperty("balRlp").GetString()!);
            if (block.EncodedBlockAccessList is { } encoded)
                block.BlockAccessList = Rlp.Decode<Nethermind.Core.BlockAccessLists.ReadOnlyBlockAccessList>(encoded);
            foreach (IBlockPreprocessorStep preprocessor in harness.Container.Resolve<IReadOnlyList<IBlockPreprocessorStep>>()) preprocessor.RecoverData(block);
            harness.Tree.SuggestBlock(block);
            harness.Container.Resolve<IBlockAccessListStore>().InsertFromBlock(block);
        }
    }

    private static Hash256? ExpectedShadowRoot(MigrationLifecycleHarness harness, string name) =>
        harness.Expected[name].GetProperty("binary").GetBoolean() ? null : new Hash256(harness.Expected[name].GetProperty("pbtRoot").GetString()!);

    private static void AssertPbtState(MigrationLifecycleHarness harness, string name)
    {
        StateId stateId = new(harness.Blocks[name].Header);
        Assert.That(harness.Pbt.HasStateForBlock(stateId), Is.True, name);
        using PbtReadOnlySnapshotBundle bundle = harness.Pbt.GatherReadOnlyBundle(stateId);
        Assert.That(bundle.TreeRoot, Is.EqualTo(new Hash256(harness.Expected[name].GetProperty("pbtRoot").GetString()!).ValueHash256), name);
    }

    private static Flat.StateId FlatState(IPersistence persistence)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        return reader.CurrentState;
    }

    private static UInt256 Parse(string value) => new(Bytes.FromHexString(value.Length % 2 == 0 ? value : "0x0" + value[2..]), true);
}

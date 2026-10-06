// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Serialization.Rlp;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.State.Pbt.Migration;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class MigrationEngineRpcE2ETests
{
    [Test]
    public async Task Unavailable_state_returns_syncing_until_processed_then_serves_pbt_but_rejects_mpt_proofs()
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        await using MigrationLifecycleHarness harness = await MigrationLifecycleHarness.Create(Path.Combine(scratch.Path, "target"), portable: true, FlatLayout.Flat,
            Path.Combine(Eip8347FixtureState.Directory, "builder-predeploys"),
            builder => builder.AddModule(new MergePluginModule()).AddSingleton<IEngineRequestsTracker, NoEngineRequestsTracker>());
        await harness.Scheduler.DisposeAsync();
        IEngineRpcModule engine = harness.Container.Resolve<IEngineRpcModule>();
        IEthRpcModule eth = harness.Container.Resolve<IRpcModuleFactory<IEthRpcModule>>().Create();
        IMigrationDebugRpcModule debug = harness.Container.Resolve<IMigrationDebugRpcModule>();
        Hash256 anchorHash = harness.Anchor.Hash!;
        Hash256 anchorShadowRoot = harness.PbtRoot("anchor");
        MigrationProgressForRpc progress = debug.debug_migrationProgress().Data;
        Assert.That(progress.Binary, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.Phase, Is.EqualTo("running"));
            Assert.That(progress.Binary!.Cursor, Is.Zero);
            Assert.That(progress.Binary.CursorHash, Is.EqualTo(anchorHash));
            Assert.That(progress.Binary.ShadowRoot, Is.EqualTo(anchorShadowRoot));
            Assert.That(progress.Merkle, Is.Null);
            Assert.That(debug.debug_shadowStateRoot(anchorHash).Data, Is.EqualTo(anchorShadowRoot));
        }

        Block[] branch = [harness.Blocks["a1"], harness.Blocks["a2"], harness.Blocks["a3"], harness.Blocks["a4"]];
        foreach (Block block in branch) harness.Tree.SuggestBlock(block);
        Block unavailable = branch[^2];
        Block head = branch[^1];
        Assert.That(harness.Reader.HasStateForBlock(unavailable.Header), Is.False);
        head.EncodedBlockAccessList = Bytes.FromHexString(harness.Expected["a4"].GetProperty("balRlp").GetString()!);
        ExecutionPayloadV4 repeatedPayload = ExecutionPayloadV4.Create(head);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ResultWrapper<PayloadStatusV1> newPayload = await engine.engine_newPayloadV5(repeatedPayload, [], head.ParentBeaconBlockRoot, []);
            Assert.That(newPayload.Result.ResultType, Is.EqualTo(ResultType.Success), newPayload.Result.Error);
            AssertSyncing(newPayload.Data);
        }

        foreach (bool build in new[] { false, true })
        {
            PayloadAttributes? attributes = build ? new PayloadAttributes
            {
                Timestamp = head.Timestamp,
                PrevRandao = head.Header.MixHash,
                SuggestedFeeRecipient = head.Beneficiary,
                Withdrawals = [],
                ParentBeaconBlockRoot = head.ParentBeaconBlockRoot,
                SlotNumber = head.SlotNumber
            } : null;
            ResultWrapper<ForkchoiceUpdatedV1Result> unavailableForkchoice = await engine.engine_forkchoiceUpdatedV4(
                new ForkchoiceStateV1(unavailable.Hash!, anchorHash, anchorHash), attributes);
            Assert.That(unavailableForkchoice.Result.ResultType, Is.EqualTo(ResultType.Success), unavailableForkchoice.Result.Error);
            AssertSyncing(unavailableForkchoice.Data.PayloadStatus);
            Assert.That(unavailableForkchoice.Data.PayloadId, Is.Null);
        }

        BlockParameter unavailableRequested = new(unavailable.Hash!);
        ResultWrapper<UInt256?> unavailableBalance = await eth.eth_getBalance(Address.Zero, unavailableRequested);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unavailableBalance.ErrorCode, Is.EqualTo(-32002));
            Assert.That(unavailableBalance.Result.Error, Is.EqualTo($"No state available for block {unavailable.Header.ToString(BlockHeader.Format.FullHashAndNumber)}"));
            Assert.That(eth.eth_getProof(Address.Zero, [], unavailableRequested).ErrorCode, Is.EqualTo(-32002));
            Assert.That(debug.debug_shadowStateRoot(unavailable.Hash!).Data, Is.Null);
            Assert.That(harness.Tree.Head!.Hash, Is.EqualTo(anchorHash), "unavailable FCU must not mutate canonical head");
            Assert.That(harness.Container.Resolve<IInvalidChainTracker>().IsOnKnownInvalidChain(head.Hash!, out _), Is.False);
            Assert.That(debug.debug_migrationProgress().Data.Binary!.CursorHash, Is.EqualTo(anchorHash));
        }

        IBlockAccessListStore accessLists = harness.Container.Resolve<IBlockAccessListStore>();
        for (int index = 0; index < branch.Length; index++)
        {
            Block block = branch[index];
            block.Header.IsPostMerge = true;
            block.EncodedBlockAccessList = Bytes.FromHexString(harness.Expected[$"a{index + 1}"].GetProperty("balRlp").GetString()!);
            block.BlockAccessList = Rlp.Decode<ReadOnlyBlockAccessList>(block.EncodedBlockAccessList);
            accessLists.Insert(block.Number, block.Hash!, block.EncodedBlockAccessList);
            foreach (IBlockPreprocessorStep preprocessor in harness.Container.Resolve<IReadOnlyList<IBlockPreprocessorStep>>()) preprocessor.RecoverData(block);
            harness.Tree.SuggestBlock(block);
        }
        IBlockchainProcessor processor = harness.Container.Resolve<IMainProcessingContext>().BlockchainProcessor;
        foreach (Block block in branch)
        {
            // The activation block needs its parent in PBT, which the background BAL replay provides.
            if (block == head) harness.WaitForPbt(branch[^2].Header);
            Block? processed = processor.Process(block, ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts, NullBlockTracer.Instance);
            Assert.That(processed?.Hash, Is.EqualTo(block.Hash));
        }
        ResultWrapper<PayloadStatusV1> recovered = await engine.engine_newPayloadV5(repeatedPayload, [], head.ParentBeaconBlockRoot, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(recovered.Data.Status, Is.EqualTo("VALID"), "the same unavailable payload must become valid after its state arrives");
            Assert.That(recovered.Data.LatestValidHash, Is.EqualTo(head.Hash));
            Assert.That(recovered.Data.ValidationError, Is.Null);
        }
        Assert.That(harness.Reader.HasStateForBlock(head.Header), Is.True);
        BlockParameter requested = new(head.Hash!);
        ResultWrapper<UInt256?> balance = await eth.eth_getBalance(Address.Zero, requested);
        Assert.That(balance.Result.ResultType, Is.EqualTo(ResultType.Success), balance.Result.Error);
        using JsonDocument allocation = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(harness.FixtureDirectory, "states", "a4.alloc.json")));
        string expectedBalance = allocation.RootElement.GetProperty(Address.Zero.ToString()).GetProperty("balance").GetString()!;
        UInt256 balanceValue = MigrationLifecycleHarness.ParseQuantity(expectedBalance);
        IResultWrapper proof = eth.eth_getProof(Address.Zero, [], requested);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoice = await engine.engine_forkchoiceUpdatedV4(
            new ForkchoiceStateV1(head.Hash!, anchorHash, anchorHash));
        Assert.That(forkchoice.Result.ResultType, Is.EqualTo(ResultType.Success), forkchoice.Result.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(balance.Data, Is.EqualTo(balanceValue));
            Assert.That(proof.ErrorCode, Is.EqualTo(-32002));
            Assert.That(proof.Result.Error, Is.EqualTo("MPT proofs are not available for the PBT state backend"));
            Assert.That(() => debug.debug_shadowStateRoot(head.Hash!).Data, Is.EqualTo(harness.ExpectedShadowRoot("a4")).After(10_000, 50),
                "the Merkle shadow follows the head through the transition window");
            Assert.That(forkchoice.Data.PayloadStatus.Status, Is.EqualTo("VALID"));
            Assert.That(forkchoice.Data.PayloadStatus.LatestValidHash, Is.EqualTo(head.Hash));
            Assert.That(forkchoice.Data.PayloadStatus.ValidationError, Is.Null);
            Assert.That(forkchoice.Data.PayloadId, Is.Null);
        }
    }

    private static void AssertSyncing(PayloadStatusV1 status)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.Status, Is.EqualTo("SYNCING"));
            Assert.That(status.LatestValidHash, Is.Null);
            Assert.That(status.ValidationError, Is.Null);
        }
    }
}

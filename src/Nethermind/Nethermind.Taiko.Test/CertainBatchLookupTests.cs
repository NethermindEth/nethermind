// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Taiko.Config;
using Nethermind.Taiko.Rpc;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Tests for taikoAuth_lastCertainBlockIDByBatchID and taikoAuth_lastCertainL1OriginByBatchID.
/// Mirrors taiko-geth PR #536 and #537 test patterns: write to rawdb, call RPC, verify result.
/// </summary>
public class CertainBatchLookupTests
{
    private IDb _db = null!;
    private L1OriginStore _store = null!;
    private TaikoEngineRpcModule _rpcModule = null!;

    [SetUp]
    public void Setup()
    {
        _db = new TestMemDb();
        _store = new L1OriginStore(_db, new L1OriginDecoder());
        _rpcModule = CreateRpcModule(_store);
    }

    /// <summary>
    /// Mirrors ethclient/taiko_api_test.go TestLastCertainBlockIDByBatchID from taiko-geth PR #536.
    /// Step 1: call with batchID=1, expect null (nothing in DB).
    /// Step 2: write batch-to-block mapping, call again, expect the written blockID.
    /// </summary>
    [Test]
    public void TestLastCertainBlockIDByBatchID()
    {
        UInt256 batchId = 1;

        // Call before any mapping exists — expect null
        ResultWrapper<UInt256?> result = _rpcModule.taikoAuth_lastCertainBlockIDByBatchID(batchId);
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Null);

        // Write the mapping: batchID=1 -> blockID=10
        UInt256 blockId = 10;
        _store.WriteBatchToLastBlockID(batchId, blockId);

        // Call again — expect the written blockID
        result = _rpcModule.taikoAuth_lastCertainBlockIDByBatchID(batchId);
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.EqualTo(blockId));
    }

    /// <summary>
    /// Mirrors eth/taiko_api_backend_test.go TestLastCertainL1OriginByBatchID from taiko-geth PR #537.
    /// Step 1: call with batchID=1, expect null.
    /// Step 2: write batch-to-block mapping (batchID=1 -> blockID=2) and L1Origin, call again, verify deep equality.
    /// </summary>
    [Test]
    public void TestLastCertainL1OriginByBatchID()
    {
        UInt256 batchId = 1;

        // Call before any mapping exists — expect null
        ResultWrapper<L1Origin?> result = _rpcModule.taikoAuth_lastCertainL1OriginByBatchID(batchId);
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Null);

        // Write the mapping and L1Origin, mirroring taiko-geth test values:
        // blockID=2, L2BlockHash=0x1, L1BlockHeight=3, L1BlockHash=0x2
        UInt256 blockId = 2;
        Hash256 l2BlockHash = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        Hash256 l1BlockHash = new("0x0000000000000000000000000000000000000000000000000000000000000002");
        L1Origin expected = new(blockId, l2BlockHash, 3, l1BlockHash, null);

        _store.WriteBatchToLastBlockID(batchId, blockId);
        _store.WriteL1Origin(blockId, expected);

        // Call again — verify all fields
        result = _rpcModule.taikoAuth_lastCertainL1OriginByBatchID(batchId);
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.BlockId, Is.EqualTo(expected.BlockId));
        Assert.That(result.Data.L2BlockHash, Is.EqualTo(expected.L2BlockHash));
        Assert.That(result.Data.L1BlockHeight, Is.EqualTo(expected.L1BlockHeight));
        Assert.That(result.Data.L1BlockHash, Is.EqualTo(expected.L1BlockHash));
    }

    /// <summary>
    /// Mirrors ethclient/taiko_api_test.go TestLastCertainL1OriginByBatchID from taiko-geth PR #537.
    /// Uses a minimal L1Origin with only BlockID and L2BlockHash set.
    /// </summary>
    [Test]
    public void TestLastCertainL1OriginByBatchID_MinimalOrigin()
    {
        UInt256 batchId = 1;

        // Call before any mapping exists — expect null
        ResultWrapper<L1Origin?> result = _rpcModule.taikoAuth_lastCertainL1OriginByBatchID(batchId);
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Null);

        // Write mapping and a minimal L1Origin (only BlockID and L2BlockHash)
        UInt256 blockId = 5;
        Hash256 l2BlockHash = new("0x00000000000000000000000000000000000000000000000000000000deadbeef");
        L1Origin expected = new(blockId, l2BlockHash, null, Hash256.Zero, null);

        _store.WriteBatchToLastBlockID(batchId, blockId);
        _store.WriteL1Origin(blockId, expected);

        // Call again — verify
        result = _rpcModule.taikoAuth_lastCertainL1OriginByBatchID(batchId);
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.BlockId, Is.EqualTo(expected.BlockId));
        Assert.That(result.Data.L2BlockHash, Is.EqualTo(expected.L2BlockHash));
    }

    /// <summary>
    /// Verifies that taikoAuth_lastCertainL1OriginByBatchID returns null when the batch mapping
    /// exists but the L1Origin for that block has not been written.
    /// </summary>
    [Test]
    public void TestLastCertainL1OriginByBatchID_MissingOrigin()
    {
        UInt256 batchId = 1;
        UInt256 blockId = 2;

        _store.WriteBatchToLastBlockID(batchId, blockId);
        // Don't write the L1Origin for blockId=2

        ResultWrapper<L1Origin?> result = _rpcModule.taikoAuth_lastCertainL1OriginByBatchID(batchId);
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Null);
    }

    /// <summary>
    /// Mirrors taiko-geth PR #558 TestBatchLookupMethodsReturnNilBelowNetworkThreshold and
    /// alethia-reth PR #177 filters_batch_lookup_results_below_network_threshold. On Mainnet,
    /// a batch whose resolved block id is strictly below the last-Pacaya block must report
    /// JSON null on all four taikoAuth_last…ByBatchID methods, even when the underlying
    /// batch-to-block and L1Origin records are present in the store.
    /// </summary>
    [Test]
    public void TestBatchLookup_MainnetBelowThresholdReturnsNull()
    {
        TaikoEngineRpcModule rpc = CreateRpcModule(_store, BatchLookupThresholds.TaikoMainnetChainId);
        UInt256 batchId = 1;
        UInt256 blockId = BatchLookupThresholds.TaikoMainnetBatchLookupThreshold - 1;
        L1Origin origin = new(blockId, Hash256.Zero, 3, Hash256.Zero, null);
        _store.WriteBatchToLastBlockID(batchId, blockId);
        _store.WriteL1Origin(blockId, origin);

        ResultWrapper<L1Origin?> lastL1Origin = rpc.taikoAuth_lastL1OriginByBatchID(batchId).Result;
        Assert.That(lastL1Origin.Result, Is.EqualTo(Result.Success));
        Assert.That(lastL1Origin.Data, Is.Null);

        ResultWrapper<UInt256?> lastBlockId = rpc.taikoAuth_lastBlockIDByBatchID(batchId).Result;
        Assert.That(lastBlockId.Result, Is.EqualTo(Result.Success));
        Assert.That(lastBlockId.Data, Is.Null);

        ResultWrapper<UInt256?> lastCertainBlockId = rpc.taikoAuth_lastCertainBlockIDByBatchID(batchId);
        Assert.That(lastCertainBlockId.Result, Is.EqualTo(Result.Success));
        Assert.That(lastCertainBlockId.Data, Is.Null);

        ResultWrapper<L1Origin?> lastCertainL1Origin = rpc.taikoAuth_lastCertainL1OriginByBatchID(batchId);
        Assert.That(lastCertainL1Origin.Result, Is.EqualTo(Result.Success));
        Assert.That(lastCertainL1Origin.Data, Is.Null);
    }

    /// <summary>
    /// The threshold is the *first* allowed block id (per taiko-geth's <c>blockID.Cmp(threshold) &lt; 0</c>
    /// and alethia-reth's <c>block_number &lt; threshold</c>). A batch resolving to exactly the
    /// threshold value must pass through unchanged on every method.
    /// </summary>
    [Test]
    public void TestBatchLookup_MainnetAtThresholdReturnsValue()
    {
        TaikoEngineRpcModule rpc = CreateRpcModule(_store, BatchLookupThresholds.TaikoMainnetChainId);
        UInt256 batchId = 1;
        UInt256 blockId = BatchLookupThresholds.TaikoMainnetBatchLookupThreshold;
        L1Origin origin = new(blockId, Hash256.Zero, 3, Hash256.Zero, null);
        _store.WriteBatchToLastBlockID(batchId, blockId);
        _store.WriteL1Origin(blockId, origin);

        Assert.That(rpc.taikoAuth_lastL1OriginByBatchID(batchId).Result.Data, Is.Not.Null);
        Assert.That(rpc.taikoAuth_lastBlockIDByBatchID(batchId).Result.Data, Is.EqualTo(blockId));
        Assert.That(rpc.taikoAuth_lastCertainBlockIDByBatchID(batchId).Data, Is.EqualTo(blockId));
        Assert.That(rpc.taikoAuth_lastCertainL1OriginByBatchID(batchId).Data, Is.Not.Null);
    }

    /// <summary>
    /// Hoodi has its own last-Pacaya threshold (3_951_005). Same gate, different value.
    /// </summary>
    [Test]
    public void TestBatchLookup_HoodiBelowThresholdReturnsNull()
    {
        TaikoEngineRpcModule rpc = CreateRpcModule(_store, BatchLookupThresholds.TaikoHoodiChainId);
        UInt256 batchId = 1;
        UInt256 blockId = BatchLookupThresholds.TaikoHoodiBatchLookupThreshold - 1;
        L1Origin origin = new(blockId, Hash256.Zero, 3, Hash256.Zero, null);
        _store.WriteBatchToLastBlockID(batchId, blockId);
        _store.WriteL1Origin(blockId, origin);

        Assert.That(rpc.taikoAuth_lastL1OriginByBatchID(batchId).Result.Data, Is.Null);
        Assert.That(rpc.taikoAuth_lastBlockIDByBatchID(batchId).Result.Data, Is.Null);
        Assert.That(rpc.taikoAuth_lastCertainBlockIDByBatchID(batchId).Data, Is.Null);
        Assert.That(rpc.taikoAuth_lastCertainL1OriginByBatchID(batchId).Data, Is.Null);
    }

    /// <summary>
    /// Devnet and Masaya have no threshold (geth: <c>threshold == 0</c>; reth: <c>None</c>).
    /// Any block id, including ones well below the Mainnet/Hoodi thresholds, must pass through.
    /// </summary>
    [TestCase(167_001UL, TestName = "Devnet")]
    [TestCase(167_011UL, TestName = "Masaya")]
    public void TestBatchLookup_UnfilteredNetworks(ulong chainId)
    {
        TaikoEngineRpcModule rpc = CreateRpcModule(_store, chainId);
        UInt256 batchId = 1;
        UInt256 blockId = 1;
        L1Origin origin = new(blockId, Hash256.Zero, 3, Hash256.Zero, null);
        _store.WriteBatchToLastBlockID(batchId, blockId);
        _store.WriteL1Origin(blockId, origin);

        Assert.That(rpc.taikoAuth_lastL1OriginByBatchID(batchId).Result.Data, Is.Not.Null);
        Assert.That(rpc.taikoAuth_lastBlockIDByBatchID(batchId).Result.Data, Is.EqualTo(blockId));
        Assert.That(rpc.taikoAuth_lastCertainBlockIDByBatchID(batchId).Data, Is.EqualTo(blockId));
        Assert.That(rpc.taikoAuth_lastCertainL1OriginByBatchID(batchId).Data, Is.Not.Null);
    }

    /// <summary>
    /// Exercises the chain-scan fallback in <c>taikoAuth_lastL1OriginByBatchID</c> /
    /// <c>taikoAuth_lastBlockIDByBatchID</c>: with no batch→block mapping in the DB, the
    /// scan walks <c>blockFinder.Head</c> backwards, decodes the Shasta proposalId from
    /// the header's ExtraData, and returns the block number on match. The threshold gate
    /// must then fire on the scan-returned block id, not just on the DB-cached one. This
    /// is a regression guard against future refactors that could reorder the gate vs.
    /// the scan branch in <c>TaikoEngineRpcModule</c>.
    /// </summary>
    [Test]
    public void TestBatchLookup_MainnetScanPathBelowThresholdReturnsNull()
    {
        UInt256 batchId = 1;
        ulong blockNumber = BatchLookupThresholds.TaikoMainnetBatchLookupThreshold - 1;

        // Shasta extraData layout: [basefeeSharingPctg=0][proposalId=batchId, 6 bytes big-endian].
        byte[] extraData = new byte[TaikoHeaderHelper.ShastaExtraDataLen];
        extraData[TaikoHeaderHelper.ShastaExtraDataLen - 1] = 1;

        BlockHeader header = Build.A.BlockHeader
            .WithNumber(blockNumber)
            .WithExtraData(extraData)
            .TestObject;
        Transaction anchorTx = Build.A.Transaction
            .WithData(TaikoBlockValidator.AnchorV4Selector)
            .TestObject;
        Block block = Build.A.Block
            .WithHeader(header)
            .WithTransactions(anchorTx)
            .TestObject;

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(block);

        // Write the L1Origin so a missing gate would yield a non-null Data on lastL1OriginByBatchID,
        // proving the gate (not a missing record) is what produces the null result.
        UInt256 blockId = (UInt256)blockNumber;
        _store.WriteL1Origin(blockId, new L1Origin(blockId, Hash256.Zero, 3, Hash256.Zero, null));
        // Deliberately do NOT WriteBatchToLastBlockID — forces the scan fallback.

        TaikoEngineRpcModule rpc = CreateRpcModule(_store, BatchLookupThresholds.TaikoMainnetChainId, blockFinder);

        // Only the scan-fallback methods are asserted: taikoAuth_lastCertain*ByBatchID have
        // no scan fallback, so with an empty batch→block mapping they already return null
        // before the threshold gate is reached and would not prove the gate fired.
        Assert.That(rpc.taikoAuth_lastL1OriginByBatchID(batchId).Result.Data, Is.Null);
        Assert.That(rpc.taikoAuth_lastBlockIDByBatchID(batchId).Result.Data, Is.Null);
    }

    /// <summary>
    /// Pins the exact threshold literals sourced from taiko-geth PR #558 and alethia-reth PR #177.
    /// These are consensus-critical values; a typo would silently allow or block entire block ranges.
    /// </summary>
    [Test]
    public void BatchLookupThresholds_are_pinned()
    {
        Assert.That(BatchLookupThresholds.TaikoMainnetBatchLookupThreshold, Is.EqualTo(4_990_434UL));
        Assert.That(BatchLookupThresholds.TaikoHoodiBatchLookupThreshold, Is.EqualTo(3_951_005UL));
    }

    private static TaikoEngineRpcModule CreateRpcModule(IL1OriginStore l1OriginStore, ulong chainId = 0, IBlockFinder? blockFinder = null)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(chainId);
        return CreateRpcModuleInternal(l1OriginStore, specProvider, blockFinder);
    }

    private static TaikoEngineRpcModule CreateRpcModuleInternal(IL1OriginStore l1OriginStore, ISpecProvider specProvider, IBlockFinder? blockFinder) => new(
        Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV4Result?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV5Result?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV6Result?>>(),
        Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(),
        Substitute.For<IForkchoiceUpdatedHandler>(),
        Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>>>(),
        Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
        Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
        Substitute.For<IHandler<IEnumerable<string>, IReadOnlyList<string>>>(),
        Substitute.For<IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>>>(),
        Substitute.For<IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?>>(),
        Substitute.For<IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?>>(),
        Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>>>(),
        Substitute.For<IGetPayloadBodiesByRangeV2Handler>(),
        Substitute.For<IHandler<Hash256, InclusionListBytes>>(),
        new Nethermind.Consensus.Transactions.InclusionListTxSource(null, null, null),
        Substitute.For<IEngineRequestsTracker>(),
        specProvider,
        null!,
        Substitute.For<ILogManager>(),
        Substitute.For<ITxPool>(),
        blockFinder ?? Substitute.For<IBlockFinder>(),
        Substitute.For<IShareableTxProcessorSource>(),
        TxDecoder.Instance,
        l1OriginStore,
        new SurgeConfig()
    );
}

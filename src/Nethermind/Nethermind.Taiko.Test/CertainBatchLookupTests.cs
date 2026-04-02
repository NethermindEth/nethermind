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
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
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

    private static TaikoEngineRpcModule CreateRpcModule(IL1OriginStore l1OriginStore) => new(
        Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV4Result?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV5Result?>>(),
        Substitute.For<IAsyncHandler<byte[], GetPayloadV6Result?>>(),
        Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(),
        Substitute.For<IForkchoiceUpdatedHandler>(),
        Substitute.For<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>>(),
        Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
        Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
        Substitute.For<IHandler<IEnumerable<string>, IEnumerable<string>>>(),
        Substitute.For<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>>(),
        Substitute.For<IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?>>(),
        Substitute.For<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>>>(),
        Substitute.For<IGetPayloadBodiesByRangeV2Handler>(),
        Substitute.For<IEngineRequestsTracker>(),
        Substitute.For<ISpecProvider>(),
        null!,
        Substitute.For<ILogManager>(),
        Substitute.For<ITxPool>(),
        Substitute.For<IBlockFinder>(),
        Substitute.For<IShareableTxProcessorSource>(),
        Substitute.For<IRlpStreamEncoder<Transaction>>(),
        l1OriginStore,
        new SurgeConfig()
    );
}

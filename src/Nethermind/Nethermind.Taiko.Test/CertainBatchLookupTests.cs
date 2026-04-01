// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Taiko.Config;
using Nethermind.Taiko.Rpc;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Tests for taikoAuth_lastCertainBlockIDByBatchID and taikoAuth_lastCertainL1OriginByBatchID.
/// These methods perform database-only lookups without blockchain traversal fallback.
/// </summary>
public class CertainBatchLookupTests
{
    private IDb _db = null!;
    private L1OriginStore _store = null!;

    [SetUp]
    public void Setup()
    {
        _db = new TestMemDb();
        _store = new L1OriginStore(_db, new L1OriginDecoder());
    }

    [Test]
    public void LastCertainBlockIDByBatchID_Returns_null_when_batch_not_in_db()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        originStore.ReadBatchToLastBlockID(Arg.Any<UInt256>()).Returns((UInt256?)null);

        TaikoEngineRpcModule module = CreateModule(originStore);

        var result = module.taikoAuth_lastCertainBlockIDByBatchID(1);

        result.Data.Should().BeNull();
    }

    [Test]
    public void LastCertainBlockIDByBatchID_Returns_blockId_when_batch_exists_in_db()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        originStore.ReadBatchToLastBlockID((UInt256)1).Returns((UInt256?)200);

        TaikoEngineRpcModule module = CreateModule(originStore);

        var result = module.taikoAuth_lastCertainBlockIDByBatchID(1);

        result.Data.Should().Be((UInt256)200);
    }

    [Test]
    public void LastCertainBlockIDByBatchID_Does_not_read_l1_origin()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        originStore.ReadBatchToLastBlockID(Arg.Any<UInt256>()).Returns((UInt256?)null);

        TaikoEngineRpcModule module = CreateModule(originStore);

        module.taikoAuth_lastCertainBlockIDByBatchID(42);

        originStore.Received(1).ReadBatchToLastBlockID((UInt256)42);
        originStore.DidNotReceive().ReadL1Origin(Arg.Any<UInt256>());
    }

    [Test]
    public void LastCertainL1OriginByBatchID_Returns_null_when_batch_not_in_db()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        originStore.ReadBatchToLastBlockID(Arg.Any<UInt256>()).Returns((UInt256?)null);

        TaikoEngineRpcModule module = CreateModule(originStore);

        var result = module.taikoAuth_lastCertainL1OriginByBatchID(1);

        result.Data.Should().BeNull();
    }

    [Test]
    public void LastCertainL1OriginByBatchID_Returns_origin_when_batch_and_origin_exist()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        L1Origin expectedOrigin = new(200, TestItem.KeccakA, 456, Hash256.Zero, null);

        originStore.ReadBatchToLastBlockID((UInt256)1).Returns((UInt256?)200);
        originStore.ReadL1Origin((UInt256)200).Returns(expectedOrigin);

        TaikoEngineRpcModule module = CreateModule(originStore);

        var result = module.taikoAuth_lastCertainL1OriginByBatchID(1);

        result.Data.Should().Be(expectedOrigin);
    }

    [Test]
    public void LastCertainL1OriginByBatchID_Returns_null_when_batch_exists_but_origin_missing()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        originStore.ReadBatchToLastBlockID((UInt256)1).Returns((UInt256?)200);
        originStore.ReadL1Origin((UInt256)200).Returns((L1Origin?)null);

        TaikoEngineRpcModule module = CreateModule(originStore);

        var result = module.taikoAuth_lastCertainL1OriginByBatchID(1);

        result.Data.Should().BeNull();
    }

    [Test]
    public void LastCertainL1OriginByBatchID_Does_not_read_origin_when_no_batch_mapping()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        originStore.ReadBatchToLastBlockID(Arg.Any<UInt256>()).Returns((UInt256?)null);

        TaikoEngineRpcModule module = CreateModule(originStore);

        module.taikoAuth_lastCertainL1OriginByBatchID(42);

        originStore.Received(1).ReadBatchToLastBlockID((UInt256)42);
        originStore.DidNotReceive().ReadL1Origin(Arg.Any<UInt256>());
    }

    [Test]
    public void Integration_LastCertainBlockIDByBatchID_with_real_store()
    {
        _store.WriteBatchToLastBlockID(5, 500);

        UInt256? blockId = _store.ReadBatchToLastBlockID(5);
        blockId.Should().Be((UInt256)500);

        UInt256? missing = _store.ReadBatchToLastBlockID(999);
        missing.Should().BeNull();
    }

    [Test]
    public void Integration_LastCertainL1OriginByBatchID_with_real_store()
    {
        UInt256 batchId = 5;
        UInt256 blockId = 500;
        L1Origin expectedOrigin = new(blockId, TestItem.KeccakB, 789, Hash256.Zero, null);

        _store.WriteBatchToLastBlockID(batchId, blockId);
        _store.WriteL1Origin(blockId, expectedOrigin);

        UInt256? readBlockId = _store.ReadBatchToLastBlockID(batchId);
        readBlockId.Should().Be(blockId);

        L1Origin? readOrigin = _store.ReadL1Origin(readBlockId!.Value);
        readOrigin.Should().NotBeNull();
        readOrigin!.BlockId.Should().Be(blockId);
        readOrigin.L1BlockHeight.Should().Be(789);
    }

    private static TaikoEngineRpcModule CreateModule(IL1OriginStore originStore)
    {
        return new TaikoEngineRpcModule(
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
            new GCKeeper(NoGCStrategy.Instance, LimboLogs.Instance),
            LimboLogs.Instance,
            Substitute.For<ITxPool>(),
            Substitute.For<IBlockFinder>(),
            Substitute.For<IShareableTxProcessorSource>(),
            Substitute.For<IRlpStreamEncoder<Transaction>>(),
            originStore,
            Substitute.For<ISurgeConfig>()
        );
    }
}

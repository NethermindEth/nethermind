// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.DataAvailability;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.Engine;

public class LocalBlobTests
{
    [Test]
    public async Task Import_uses_local_blobs_before_waiting_for_the_network([Values] bool validSignature)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create(blobCount: 1);
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        EngineTests.ConfigureEngine(engine, () => Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        DataColumnKzgFixture.BlobFixture blob = DataColumnKzgFixture.BuildBlob(0x10);
        engine.engine_getBlobsV2(Arg.Any<byte[][]>()).Returns(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(
            [new(DataColumnKzgFixture.MakeBlob(0x10), [.. Enumerable.Range(0, 128).Select(index => DataColumnKzgFixture.ProofAt(blob, index).AsSpan().ToArray())])]));
        BeaconChainConfig config = new() { Discv5Port = 0 };
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, chain.Spec);
        await using BeaconDiscovery discovery = new(config, chain.Spec, store, new RangeSyncTests.FixedIPResolver(System.Net.IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(System.Net.IPAddress.Loopback);
        DataColumnSidecarPool pool = new();
        BlockImporterFactory factory = new(chain.Spec, store, chain.Pubkeys, TestEngineDriver.Create(EngineTests.CreateDetector(engine, out _)),
            config, LimboLogs.Instance, pool, chain.ClockAtSlot(1), discovery);
        IBlockImporter importer = factory.Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);
        if (!validSignature) chain.Block.Signature = default;

        BlockImportResult result = importer.ImportRequested(new ForkedSignedBeaconBlock.OfFulu(chain.Block), chain.BlockRoot);

        Assert.That(result, Is.EqualTo(validSignature ? BlockImportResult.Imported : BlockImportResult.Invalid));
        await engine.Received(validSignature ? 1 : 0).engine_getBlobsV2(Arg.Any<byte[][]>());
        for (ulong column = 0; column < 128; column++) Assert.That(pool.TryGet(chain.BlockRoot, column, out _), Is.EqualTo(validSignature));
    }

    [Test]
    public async Task Local_blobs_become_verified_columns_and_release_the_engine_response([Values(0, 1, 2, 3)] int fault)
    {
        ImportableBlobBlock block = ImportableBlobBlock.Create(blobCount: 1);
        byte seed = fault == 1 ? (byte)0x20 : (byte)0x10;
        DataColumnKzgFixture.BlobFixture blob = DataColumnKzgFixture.BuildBlob(seed);
        using OwnedResponse response = new()
        {
            fault == 2 ? null : new BlobAndProofV2(DataColumnKzgFixture.MakeBlob(seed),
                [.. Enumerable.Range(0, fault == 3 ? 127 : 128).Select(index => DataColumnKzgFixture.ProofAt(blob, index).AsSpan().ToArray())]),
        };
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_getBlobsV2(Arg.Any<byte[][]>()).Returns(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(response));
        ExternalClDetector detector = EngineTests.CreateDetector(engine, out _);
        EngineDriver driver = TestEngineDriver.Create(detector);

        DataColumnSidecar[] columns = await driver.GetLocalColumns(block.Block);

        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(response.Disposed, Is.True);
        Assert.That(detector.IsExternalClDetected, Is.False);
        Assert.That(columns, Has.Length.EqualTo(fault == 0 ? 128 : 0));
        foreach (DataColumnSidecar column in columns)
        {
            Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(column), Is.True);
            Assert.That(DataColumnSidecar.Encode(column), Is.EqualTo(DataColumnSidecar.Encode(block.Columns[column.Index])));
        }
    }

    [Test]
    public async Task Slow_local_blob_retrieval_is_bounded_and_its_late_response_is_disposed([Values] bool blocksSynchronously)
    {
        ImportableBlobBlock block = ImportableBlobBlock.Create(blobCount: 1);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using OwnedResponse response = [];
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_getBlobsV2(Arg.Any<byte[][]>()).Returns(_ =>
        {
            started.TrySetResult();
            return blocksSynchronously ? Task.FromResult(completion.Task.GetAwaiter().GetResult()) : completion.Task;
        });
        ExternalClDetector detector = EngineTests.CreateDetector(engine, out _);
        EngineDriver driver = TestEngineDriver.Create(detector, localColumnsTimeout: TimeSpan.Zero);
        Task<DataColumnSidecar[]> lookup = Task.Run(() => driver.GetLocalColumns(block.Block));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await lookup.WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
            Assert.That(await driver.GetLocalColumns(block.Block).WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
            _ = engine.Received(1).engine_getBlobsV2(Arg.Any<byte[][]>());
            Assert.That(detector.WaitForEmbeddedCallsAsync().IsCompleted, Is.False, "shutdown must still drain the timed-out call");
        }
        finally
        {
            completion.TrySetResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(response));
            await lookup.WaitAsync(TimeSpan.FromSeconds(5));
            await detector.WaitForEmbeddedCallsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.That(response.Disposed, Is.True);
    }

    private sealed class OwnedResponse : List<BlobAndProofV2?>, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}

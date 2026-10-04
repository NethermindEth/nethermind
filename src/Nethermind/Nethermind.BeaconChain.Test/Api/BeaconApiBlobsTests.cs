// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Autofac;
using CkzgLib;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Api.Endpoints;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.DataAvailability;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// beacon-APIs v5.0.0-alpha.2 <c>getBlobs</c> (apis/beacon/blobs/blobs.yaml): blobs rebuilt from the stored data columns of a block,
/// directly from columns 0 to 63 or through cell recovery from any other half (fulu/das-core.md recover_matrix).
/// </summary>
public class BeaconApiBlobsTests : BeaconApiFixture
{
    private const string Json = "application/json";
    private const string OctetStream = "application/octet-stream";
    private const byte FirstSeed = 0x20;
    private const byte SecondSeed = 0x40;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Sepolia;
    private static readonly ulong FuluSlot = Spec.FuluForkEpoch * Spec.SlotsPerEpoch + 5;
    private static readonly ulong GloasSlot = Spec.GloasForkEpoch * Spec.SlotsPerEpoch + 5;

    private static readonly Hash256 SystematicRoot = TestRoot(0xb1);
    private static readonly Hash256 RecoveryRoot = TestRoot(0xb2);
    private static readonly Hash256 InterleavedRoot = TestRoot(0xb3);
    private static readonly Hash256 CustodyOnlyRoot = TestRoot(0xb4);
    private static readonly Hash256 NoBlobsRoot = TestRoot(0xb5);
    private static readonly Hash256 GloasRoot = TestRoot(0xb6);
    private static readonly Hash256 DamagedRoot = TestRoot(0xb7);

    private SszKzgCommitment[] _commitments = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        // Fork choice verifies the Fulu block and the Gloas payload; the Gloas block's own status stays optimistic to show it is not what decides.
        ForkChoiceSnapshotHolder forkChoice = new()
        {
            Current = new ForkChoiceSnapshot(new CheckpointRef(0, SystematicRoot), new CheckpointRef(0, SystematicRoot), Hash256.Zero,
            [
                new ForkChoiceSnapshotNode(FuluSlot, SystematicRoot, null, 0, 0, 0, ExecutionStatus.Valid, FilledHash(0x01), PayloadValid: true),
                new ForkChoiceSnapshotNode(GloasSlot, GloasRoot, null, 0, 0, 0, ExecutionStatus.Optimistic, FilledHash(0x02), PayloadValid: true),
            ]),
        };
        _host = await StartAsync(Spec, forkChoice);
        DataColumnKzgFixture.BlobFixture[] blobs = [DataColumnKzgFixture.BuildBlob(FirstSeed), DataColumnKzgFixture.BuildBlob(SecondSeed)];
        _commitments = [.. blobs.Select(DataColumnKzgFixture.CommitmentOf)];

        PutFulu(SystematicRoot, blobs, Enumerable.Range(0, 64));
        PutFulu(RecoveryRoot, blobs, Enumerable.Range(64, 64));
        PutFulu(InterleavedRoot, blobs, Enumerable.Range(0, 64).Select(i => 2 * i + 1));
        PutFulu(CustodyOnlyRoot, blobs, Enumerable.Range(0, 63).Select(i => 2 * i));
        PutFulu(NoBlobsRoot, [], []);
        PutFulu(DamagedRoot, blobs, Enumerable.Range(0, 64));
        // An extra cell still lets both rows be read, so only the cell count check refuses this column.
        _host.Store.PutDataColumnSidecar(DamagedRoot, FuluSlot, FuluColumn([blobs[1], blobs[0], blobs[1]], _commitments, 5));

        _host.Store.PutForkedBlock(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(GloasBlock(GloasSlot, _commitments)));
        foreach (int column in Enumerable.Range(64, 64))
        {
            _host.Store.PutDataColumnSidecar(new DataColumnSidecarGloas
            {
                Index = (ulong)column,
                Column = [.. blobs.Select(b => DataColumnKzgFixture.CellAt(b, column))],
                KzgProofs = [.. blobs.Select(b => DataColumnKzgFixture.ProofAt(b, column))],
                Slot = GloasSlot,
                BeaconBlockRoot = GloasRoot,
            });
        }
    }

    [Test]
    public async Task Accepted_columns_serve_blobs_before_the_store_writer_drains([Values] bool gloas, [Values(0, 32)] int storedColumns)
    {
        ContainerBuilder builder = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia)
            .AddModule(new BeaconApiModule())
            .AddSingleton<IBeaconApiConfig>(new BeaconApiConfig { Enabled = true, Host = "127.0.0.1", Port = 0 })
            .AddSingleton<IProcessExitSource>(new NoOpProcessExitSource())
            .AddSingleton<IEngineDriver>(new NoOpEngineDriver())
            .AddSingleton(Spec);
        builder.RegisterType<DataColumnSidecarPool>().WithParameter("clock", null!).SingleInstance();
        await using IContainer container = builder.Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        DataColumnSidecarPool pool = container.Resolve<DataColumnSidecarPool>();
        ColumnStoreWriter writer = container.Resolve<ColumnStoreWriter>();
        BeaconApiHost host = container.Resolve<BeaconApiHost>();
        await host.StartAsync(CancellationToken.None);
        using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        DataColumnKzgFixture.BlobFixture[] blobs = [DataColumnKzgFixture.BuildBlob(FirstSeed)];
        SszKzgCommitment[] commitments = [.. blobs.Select(DataColumnKzgFixture.CommitmentOf)];
        Hash256 root = gloas ? GloasRoot : SystematicRoot;
        ulong slot = gloas ? GloasSlot : FuluSlot;
        SignedBeaconBlock block = MinimalBlock(slot);
        block.Message!.Body!.BlobKzgCommitments = commitments;
        store.PutForkedBlock(root, gloas
            ? new ForkedSignedBeaconBlock.OfGloas(GloasBlock(slot, commitments))
            : new ForkedSignedBeaconBlock.OfFulu(block));

        using ManualResetEventSlim release = new();
        writer.Post(() => release.Wait());
        try
        {
            for (int column = 0; column < Eip7594DasConstants.RequiredColumnsForReconstruction; column++)
            {
                if (gloas)
                {
                    DataColumnSidecarGloas sidecar = GloasColumn(column);
                    if (column < storedColumns) store.PutDataColumnSidecar(sidecar);
                    else Assert.That(pool.AddPendingGloas(sidecar, slot), Is.True);
                }
                else if (column < storedColumns)
                {
                    store.PutDataColumnSidecar(root, slot, FuluColumn(blobs, commitments, column));
                }
            }

            using HttpResponseMessage unaccepted = await client.GetAsync($"/eth/v1/beacon/blobs/{root}");
            Assert.That(unaccepted.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "unverified candidates must not make blobs available");

            for (int column = storedColumns; column < Eip7594DasConstants.RequiredColumnsForReconstruction; column++)
            {
                if (gloas) pool.AddGloas(GloasColumn(column));
                else pool.Add(root, slot, FuluColumn(blobs, commitments, column));
            }

            using HttpResponseMessage accepted = await client.GetAsync($"/eth/v1/beacon/blobs/{root}");
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK), await accepted.Content.ReadAsStringAsync());
            using JsonDocument body = await ReadJsonAsync(accepted);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(body.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetString()),
                    Is.EqualTo(new[] { DataColumnKzgFixture.MakeBlob(FirstSeed).ToHexString(true) }));
                Assert.That(store.HasDataColumnRecord(root, (ulong)storedColumns), Is.False, "the response must not wait for persistence");
            }
        }
        finally
        {
            release.Set();
            await writer.WhenWritten().WaitAsync(TimeSpan.FromSeconds(30));
        }

        DataColumnSidecarGloas GloasColumn(int column) => new()
        {
            Index = (ulong)column,
            Column = [DataColumnKzgFixture.CellAt(blobs[0], column)],
            KzgProofs = [DataColumnKzgFixture.ProofAt(blobs[0], column)],
            Slot = slot,
            BeaconBlockRoot = root,
        };
    }

    /// <summary>
    /// Columns 0 to 63 are the blobs themselves; any other half recovers them (consensus-specs v1.6.0
    /// fulu/polynomial-commitments-sampling.md coset_for_cell). Each held set must yield the original blobs byte for byte.
    /// </summary>
    [Test]
    public async Task Blobs_rebuilt_from_any_half_of_the_columns_are_the_original_blobs(
        [Values("systematic", "recovery", "interleaved", "gloas")] string held, [Values(Json, OctetStream)] string accept)
    {
        Hash256 root = held switch
        {
            "systematic" => SystematicRoot,
            "recovery" => RecoveryRoot,
            "interleaved" => InterleavedRoot,
            _ => GloasRoot,
        };

        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/blobs/{root}", accept);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());

        byte[][] expected = [DataColumnKzgFixture.MakeBlob(FirstSeed), DataColumnKzgFixture.MakeBlob(SecondSeed)];
        if (accept == OctetStream)
        {
            Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo(OctetStream));
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected.SelectMany(b => b).ToArray()));
            return;
        }

        JsonElement body = (await ReadJsonAsync(response)).RootElement;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(body.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "execution_optimistic", "finalized", "data" }));
        Assert.That(body.GetProperty("data").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(expected.Select(b => b.ToHexString(true))));
        Assert.That(body.GetProperty("execution_optimistic").GetBoolean(), Is.EqualTo(root == RecoveryRoot || root == InterleavedRoot),
            "types/primitive.yaml ExecutionOptimistic: a verified block payload, or for Gloas the verified bid payload, is not optimistic");
        Assert.That(body.GetProperty("finalized").GetBoolean(), Is.False);
    }

    /// <summary>blobs.yaml versioned_hashes: only the named blobs, in commitment order; an unknown hash selects nothing.</summary>
    [TestCase("{1}", new[] { SecondSeed })]
    [TestCase("{1}&versioned_hashes={0}", new[] { FirstSeed, SecondSeed })]
    [TestCase("{1},{0}", new[] { FirstSeed, SecondSeed })]
    [TestCase("0x01000000000000000000000000000000000000000000000000000000000000ee", new byte[0])]
    public async Task Versioned_hashes_select_blobs_in_commitment_order(string query, byte[] expectedSeeds)
    {
        Hash256?[] hashes = PayloadConverter.ToBlobVersionedHashes(_commitments);
        string path = $"/eth/v1/beacon/blobs/{RecoveryRoot}?versioned_hashes=" + string.Format(query, hashes[0], hashes[1]);

        HttpResponseMessage response = await _host.GetAsync(path, OctetStream);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(expectedSeeds.SelectMany(DataColumnKzgFixture.MakeBlob).ToArray()));
    }

    [Test]
    public async Task Block_without_blobs_is_an_empty_list([Values(Json, OctetStream)] string accept)
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/blobs/{NoBlobsRoot}", accept);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        if (accept == OctetStream)
        {
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
        }
        else
        {
            Assert.That((await ReadJsonAsync(response)).RootElement.GetProperty("data").GetArrayLength(), Is.Zero);
        }
    }

    /// <summary>
    /// blobs.yaml: 400 for an id or query that cannot be parsed, a repeated hash included (uniqueItems), 404 for an unknown block or a node holding
    /// fewer than half of the columns (only nodes holding every column must serve blobs), 406 for an unsupported Accept, and 500 for a stored
    /// column that does not match its block.
    /// </summary>
    [TestCase("not-a-block", Json, HttpStatusCode.BadRequest)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000b2?versioned_hashes=0x12", Json, HttpStatusCode.BadRequest)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000b2?versioned_hashes=0x01000000000000000000000000000000000000000000000000000000000000ee,0x01000000000000000000000000000000000000000000000000000000000000ee", Json, HttpStatusCode.BadRequest)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000ee", Json, HttpStatusCode.NotFound)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000b4", Json, HttpStatusCode.NotFound)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000b4", OctetStream, HttpStatusCode.NotFound)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000b2", "text/plain", HttpStatusCode.NotAcceptable)]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000b7", Json, HttpStatusCode.InternalServerError)]
    public async Task Errors_follow_the_published_responses(string blockIdAndQuery, string accept, HttpStatusCode expected)
    {
        HttpResponseMessage response = await _host.GetAsync($"/eth/v1/beacon/blobs/{blockIdAndQuery}", accept);

        await BeaconApiTestHost.AssertErrorAsync(response, expected);
    }

    /// <summary>Rebuilds past <see cref="BlobsEndpoint.MaxConcurrentRebuilds"/> are refused at once with 503, and a released permit serves again.</summary>
    [Test]
    public async Task Rebuilds_past_the_bound_are_refused_until_one_completes()
    {
        ManualTimestamper time = new(DateTimeOffset.FromUnixTimeSeconds((long)Spec.GenesisTime).UtcDateTime);
        BeaconApiContext ctx = new(new BeaconChainConfig(), Spec, _host.StatusHolder, new SlotClock(Spec, time), _host.Store,
            new LocalMetadataSource(), new NoOpEngineDriver(), LimboLogs.Instance, null, null, null);
        List<RateLimitLease> leases = [.. Enumerable.Range(0, BlobsEndpoint.MaxConcurrentRebuilds).Select(_ => ctx.BlobRebuilds.AttemptAcquire())];
        Assert.That(leases.Select(l => l.IsAcquired), Is.All.True, "fixture");

        DefaultHttpContext busy = Request();
        await BlobsEndpoint.GetBlobs(busy, SystematicRoot.ToString(), ctx);
        leases[0].Dispose();
        DefaultHttpContext served = Request();
        await BlobsEndpoint.GetBlobs(served, SystematicRoot.ToString(), ctx);
        leases[1].Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(busy.Response.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
            Assert.That(served.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(((MemoryStream)served.Response.Body).ToArray(), Has.Length.EqualTo(2 * Ckzg.BytesPerBlob));
        }

        static DefaultHttpContext Request()
        {
            DefaultHttpContext context = new();
            context.Request.Headers.Accept = OctetStream;
            context.Response.Body = new MemoryStream();
            return context;
        }
    }

    private void PutFulu(Hash256 root, DataColumnKzgFixture.BlobFixture[] blobs, IEnumerable<int> columns)
    {
        SignedBeaconBlock block = MinimalBlock(FuluSlot);
        SszKzgCommitment[] commitments = [.. blobs.Select(DataColumnKzgFixture.CommitmentOf)];
        block.Message!.Body!.BlobKzgCommitments = commitments;
        _host.Store.PutBlock(root, block);
        foreach (int column in columns)
        {
            _host.Store.PutDataColumnSidecar(root, FuluSlot, FuluColumn(blobs, commitments, column));
        }
    }

    private static DataColumnSidecar FuluColumn(DataColumnKzgFixture.BlobFixture[] blobs, SszKzgCommitment[] commitments, int column) => new()
    {
        Index = (ulong)column,
        Column = [.. blobs.Select(b => DataColumnKzgFixture.CellAt(b, column))],
        KzgCommitments = commitments,
        KzgProofs = [.. blobs.Select(b => DataColumnKzgFixture.ProofAt(b, column))],
        SignedBlockHeader = new SignedBeaconBlockHeader
        {
            Message = new BeaconBlockHeader { Slot = FuluSlot, ParentRoot = Hash256.Zero, StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero },
            Signature = FilledSignature(0x00),
        },
        KzgCommitmentsInclusionProof = [.. Enumerable.Repeat(Hash256.Zero, Eip7594DasConstants.KzgCommitmentsInclusionProofDepth)],
    };

    private static SignedBeaconBlockGloas GloasBlock(ulong slot, SszKzgCommitment[] commitments)
    {
        SignedBeaconBlockGloas block = SignedBeaconBlockBuilders.CreateMinimalGloasBlock(slot);
        block.Message!.ProposerIndex = 0;
        ExecutionPayloadBid bid = block.Message.Body!.SignedExecutionPayloadBid!.Message!;
        bid.ParentBlockHash = FilledHash(0x81);
        bid.BlockHash = FilledHash(0x02);
        bid.GasLimit = 0;
        bid.BuilderIndex = 0;
        bid.Slot = 0;
        bid.BlobKzgCommitments = commitments;
        block.Message.Body.ParentExecutionRequests = new ExecutionRequestsGloas { Deposits = [], Withdrawals = [], Consolidations = [], BuilderDeposits = [], BuilderExits = [] };
        return block;
    }
}

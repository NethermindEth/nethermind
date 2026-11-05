// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Prove;
using CapabilitiesResponse = Nethermind.Eez.Prove.CapabilitiesResponse;
using CapabilitiesRequest = Nethermind.Eez.Prove.CapabilitiesRequest;
using Nethermind.Eez.Prove.V2;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using ProverV1 = Nethermind.Eez.Prove.Prover;
using WirePostBatch = Nethermind.Eez.Prove.PostBatch;
using WirePostingContext = Nethermind.Eez.Prove.L1PostingContext;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXStreamingConformanceTests
{
    [TestCase("baseline-lookup.json", TestName = "ProveV1_RecordedLookupWindow_ReturnsIndependentProof")]
    [TestCase("baseline-static.json", TestName = "ProveV1_RecordedStaticWindow_ReturnsIndependentProof")]
    public async Task ProveV1_InlineRecordedWindow_ReturnsIndependentProof(string name)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, name);
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement);
        using GrpcChannel channel = server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());
        List<ProveChunk> chunks = NativeWindow(document.RootElement, capabilities.ValidationIdentity);

        ProveResponse response = await Prove(client, chunks);

        Proof expected = RecordedProof(document.RootElement).Proof;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.PublicInputsHash, Is.EqualTo(expected.PublicInputsHash), "v1 independently recomputes the unchanged recorded digest");
            Assert.That(response.Signature, Is.EqualTo(expected.Proof_), "the real native finalizer returns the independent recorded signature");
        }
        await server.Admission.WaitUntilIdle();
    }

    [TestCase("identity", StatusCode.FailedPrecondition, TestName = "ProveV1_DifferentValidatorIdentity_Rejects")]
    [TestCase("provenance", StatusCode.InvalidArgument, TestName = "ProveV1_MissingProvenance_Rejects")]
    [TestCase("blobs", StatusCode.InvalidArgument, TestName = "ProveV1_MissingPhysicalBlobs_Rejects")]
    [TestCase("posting", StatusCode.InvalidArgument, TestName = "ProveV1_MissingPostingContext_Rejects")]
    [TestCase("legacyHash", StatusCode.InvalidArgument, TestName = "ProveV1_LegacyClaimedDigest_Rejects")]
    [TestCase("legacyL1", StatusCode.InvalidArgument, TestName = "ProveV1_LegacyL1Hash_Rejects")]
    [TestCase("predecessor", StatusCode.InvalidArgument, TestName = "ProveV1_WrongPredecessorNumber_Rejects")]
    [TestCase("genesisAcceptance", StatusCode.FailedPrecondition, TestName = "ProveV1_FalseGenesisAcceptance_Rejects")]
    [TestCase("extraEndpoint", StatusCode.FailedPrecondition, TestName = "ProveV1_UnexpectedLiveEndpoint_Rejects")]
    [TestCase("cached", StatusCode.InvalidArgument, TestName = "ProveV1_CachedReference_Rejects")]
    [TestCase("lookup", StatusCode.FailedPrecondition, TestName = "ProveV1_FalseLookup_Rejects")]
    [TestCase("anchor", StatusCode.FailedPrecondition, TestName = "ProveV1_FalseSettlementAnchor_Rejects")]
    [TestCase("reorg", StatusCode.Aborted, TestName = "ProveV1_ReorgBeforeSigning_Rejects")]
    public async Task ProveV1_InvalidNativeEvidence_ReturnsNoProof(string mutation, StatusCode expected)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement, mutation == "reorg");
        using GrpcChannel channel = server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());
        List<ProveChunk> chunks = NativeWindow(document.RootElement, capabilities.ValidationIdentity, mutation);

        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Prove(client, chunks)))!;

        Assert.That(exception.StatusCode, Is.EqualTo(expected), "native v1 must enforce the same authority and execution gates as v2");
        await server.Admission.WaitUntilIdle();
    }

    [Test]
    public async Task Capabilities_NativeWorkerBusy_ReturnsEffectivePreparationLimits()
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement);
        using GrpcChannel channel = server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        using BackendAdmission.BackendLease slot = server.Admission.TryAcquire()!;
        Assert.That(slot, Is.Not.Null, "precondition: a worker occupies the shared backend");

        CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());

        JsonElement profile = document.RootElement.GetProperty("server_profile");
        using (Assert.EnterMultipleScope())
        {
            Assert.That((capabilities.OperationsTag, capabilities.ProfileId, capabilities.ValidationIdentity.Length), Is.EqualTo((0U, 1U, 32)),
                "the native implementation has its own validation identity");
            Assert.That((capabilities.MaxWindowBlocks, capabilities.MaxRequestBytes, capabilities.MaxRequestWitnessItems),
                Is.EqualTo((profile.GetProperty("max_window_blocks").GetUInt64(), profile.GetProperty("max_window_bytes").GetUInt64(),
                    profile.GetProperty("max_window_witness_items").GetUInt64())), "advertised admission quotas match the running server");
            Assert.That((capabilities.PreparationMaxBlocks, capabilities.CacheMaxEntries, capabilities.CacheMaxBytes,
                capabilities.MaximumLeases, capabilities.LeaseLifetimeMillis),
                Is.EqualTo((Math.Min(64UL, capabilities.MaxWindowBlocks), 131072UL, 512UL * 1024 * 1024, 8UL,
                    Math.Max(120000UL, capabilities.RequestTimeoutMillis * 2))), "advertised preparation capacity must match its bounded cache");
        }
        using AsyncClientStreamingCall<PrepareChunk, PrepareResponse> prepare = client.PrepareOrdinary();
        try { await prepare.RequestStream.CompleteAsync(); }
        catch (RpcException) { }
        RpcException preparation = (await Assert.ThrowsAsync<RpcException>(async () => await prepare.ResponseAsync))!;
        RpcException retention = (await Assert.ThrowsAsync<RpcException>(async () => await client.RetainOrdinaryRangesAsync(new RetainOrdinaryRangesRequest())))!;
        Assert.That((preparation.StatusCode, retention.StatusCode), Is.EqualTo((StatusCode.InvalidArgument, StatusCode.InvalidArgument)));
    }

    [Test]
    public void Start_NativeHeaderAboveByteQuota_RejectsBeforeWindowAdmission()
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        ByteString identity = ByteString.CopyFrom(TestItem.KeccakA.Bytes);
        ProveChunk header = NativeWindow(document.RootElement, identity)[0];
        long bytes = header.CalculateSize();
        WindowLimits limit = WindowLimits.Default with { MaxBytes = bytes };
        WindowAssembler exact = WindowAssembler.Start(limit, header, identity);
        exact.Abandon();

        RpcException exception = Assert.Throws<RpcException>(() => WindowAssembler.Start(limit with { MaxBytes = bytes - 1 }, header, identity))!;

        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.ResourceExhausted),
            "physical blobs and provenance must count before native window admission");
    }

    private static List<ProveChunk> NativeWindow(JsonElement scenario, ByteString identity, string? mutation = null)
    {
        ClientFrame[] requests = scenario.GetProperty("events").EnumerateArray()
            .Where(static item => item.GetProperty("direction").GetString() == "client")
            .Select(static item => ClientFrame.Parser.ParseFrom(Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!))).ToArray();
        Begin begin = requests.Single(static frame => frame.Begin is not null).Begin;
        BlockWitness[] blocks = requests.Where(static frame => frame.Block is not null).Select(static frame => frame.Block.Data).ToArray();
        Finalize selection = requests.Single(static frame => frame.Finalize is not null).Finalize;
        BlockHeader parent = blocks[0].Witness.Headers.Select(static bytes => Rlp.Decode<BlockHeader>(bytes.ToByteArray())!)
            .Single(header => header.Hash!.Bytes.SequenceEqual(begin.AnchorHash.Span));
        Assert.That(parent.Number, Is.Zero, "precondition: these canonical fixture baselines start from their registered genesis");
        if (mutation is "lookup" or "anchor") MutateSettlement(selection.Settlement, mutation);
        WirePostBatch batch = new() { AbiCalldata = selection.Settlement.AbiCalldata };
        batch.Blobs.Add(selection.Settlement.Blobs);
        batch.PostingContext = WirePostingContext.Parser.ParseFrom(selection.Settlement.PostingContext.ToByteArray());
        ProveHeader header = new()
        {
            RollupId = begin.RollupId,
            FromBlock = selection.FromBlock,
            ToBlock = selection.ToBlock,
            PostBatch = batch,
            ExpectedValidationIdentity = identity,
            ParentProvenance = new ParentProvenance { ParentHeaderRlp = ByteString.CopyFrom(Rlp.Encode(parent).Bytes) },
        };
        List<ProveChunk> chunks = [new ProveChunk { Header = header }];
        chunks.AddRange(blocks.Select(static block => new ProveChunk { Block = block }));
        switch (mutation)
        {
            case "identity": header.ExpectedValidationIdentity = ByteString.CopyFrom(TestItem.KeccakA.Bytes); break;
            case "provenance": header.ParentProvenance = null; break;
            case "blobs": batch.Blobs.Clear(); break;
            case "posting": batch.PostingContext = null; break;
            case "legacyHash": batch.PublicInputsHash = ByteString.CopyFrom(TestItem.KeccakA.Bytes); break;
            case "legacyL1": batch.L1BlockHash = ByteString.CopyFrom(TestItem.KeccakA.Bytes); break;
            case "predecessor": header.FromBlock++; break;
            case "genesisAcceptance": header.ParentProvenance.AcceptedL1Number = 1; break;
            case "extraEndpoint": header.ParentProvenance.LiveSlotEndpointHeadersRlp.Add(header.ParentProvenance.ParentHeaderRlp); break;
            case "cached": chunks[1] = new ProveChunk { CachedRange = new CachedOrdinaryRange() }; break;
        }
        return chunks;
    }

    private static async Task<ProveResponse> Prove(ProverV1.ProverClient client, IEnumerable<ProveChunk> chunks)
    {
        using AsyncClientStreamingCall<ProveChunk, ProveResponse> call = client.Prove(new CallOptions(deadline: DateTime.UtcNow.AddMinutes(2)));
        try
        {
            foreach (ProveChunk chunk in chunks) await call.RequestStream.WriteAsync(chunk);
            await call.RequestStream.CompleteAsync();
        }
        catch (RpcException) { }
        catch (InvalidOperationException) { }
        return await call.ResponseAsync;
    }
}

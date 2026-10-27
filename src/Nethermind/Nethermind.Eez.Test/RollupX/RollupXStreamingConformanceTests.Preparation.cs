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
using Nethermind.Eez.Execution;
using Nethermind.Eez.Prove;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using ProverV1 = Nethermind.Eez.Prove.Prover;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXStreamingConformanceTests
{
    [TestCase("baseline-lookup.json")]
    [TestCase("baseline-static.json")]
    public async Task ProveV1_PreparedOrdinaryRange_PreservesRecordedProof(string name)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, name);
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement);
        using GrpcChannel channel = server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());
        List<ProveChunk> cold = NativeWindow(document.RootElement, capabilities.ValidationIdentity);
        PrepareResponse prepared = await PrepareWindow(client, cold, capabilities.ValidationIdentity);
        List<ProveChunk> cached = [cold[0], new ProveChunk { CachedRange = PreparedRange(prepared) }, cold[^1]];

        ProveResponse response = await Prove(client, cached);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.PublicInputsHash, Is.EqualTo(RecordedProof(document.RootElement).Proof.PublicInputsHash));
            Assert.That(response.Signature, Is.EqualTo(RecordedProof(document.RootElement).Proof.Proof_));
            Assert.That(prepared.LeaseId.Length, Is.EqualTo(32));
            Assert.That(prepared.PreparedCount, Is.EqualTo((ulong)cold.Count - 2));
        }
        RetainOrdinaryRangesRequest retain = new() { ValidationIdentity = capabilities.ValidationIdentity, LeaseId = prepared.LeaseId };
        retain.Ranges.Add(PreparedRange(prepared));
        Assert.That((await client.RetainOrdinaryRangesAsync(retain)).RetainedCount, Is.EqualTo(prepared.PreparedCount));
        retain.Ranges.Clear();
        retain.Release = true;
        Assert.That((await client.RetainOrdinaryRangesAsync(retain)).RetainedCount, Is.Zero);
        ProveResponse released = await Prove(client, cached);
        Assert.That(released.Signature, Is.EqualTo(response.Signature));
    }

    [Test]
    public async Task ProveV1_PreparedRange_StillRechecksCanonicalL1()
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement, true);
        using GrpcChannel channel = server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());
        List<ProveChunk> cold = NativeWindow(document.RootElement, capabilities.ValidationIdentity);
        PrepareResponse prepared = await PrepareWindow(client, cold, capabilities.ValidationIdentity);

        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Prove(client,
            [cold[0], new ProveChunk { CachedRange = PreparedRange(prepared) }, cold[^1]])))!;

        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Aborted));
    }

    [TestCase("incomplete", StatusCode.InvalidArgument)]
    [TestCase("number", StatusCode.InvalidArgument)]
    [TestCase("hash", StatusCode.InvalidArgument)]
    [TestCase("witness", StatusCode.InvalidArgument)]
    [TestCase("header", StatusCode.InvalidArgument)]
    [TestCase("witnessContent", StatusCode.FailedPrecondition)]
    [TestCase("identity", StatusCode.FailedPrecondition)]
    [TestCase("rollup", StatusCode.FailedPrecondition)]
    [TestCase("bounds", StatusCode.InvalidArgument)]
    public async Task PrepareOrdinary_InvalidRange_DoesNotPartiallyPublish(string mutation, StatusCode expected)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement);
        using GrpcChannel channel = server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());
        List<ProveChunk> cold = NativeWindow(document.RootElement, capabilities.ValidationIdentity);
        List<PrepareChunk> chunks = Preparation(cold, capabilities.ValidationIdentity);
        Assert.That(chunks.Count, Is.GreaterThan(2));
        switch (mutation)
        {
            case "incomplete": chunks.RemoveAt(chunks.Count - 1); break;
            case "number": chunks[^1].Block.Number++; break;
            case "hash": chunks[^1].Block.Hash = ByteString.CopyFrom(new byte[32]); break;
            case "witness": chunks[^1].Block.Witness = null; break;
            case "witnessContent": chunks[^1].Block.Witness = new ExecutionWitness(); break;
            case "identity": chunks[0].Header.ExpectedValidationIdentity = ByteString.CopyFrom(new byte[32]); break;
            case "rollup": chunks[0].Header.RollupId++; break;
            case "bounds": chunks[0].Header.ToBlock = chunks[0].Header.FromBlock + 64; break;
            case "header": chunks[^1] = chunks[0].Clone(); break;
        }

        RpcException failure = (await Assert.ThrowsAsync<RpcException>(() => Prepare(client, chunks)))!;

        Assert.That(failure.StatusCode, Is.EqualTo(expected));
        BlockWitness first = cold[1].Block;
        CachedOrdinaryRange missing = new()
        {
            ValidationIdentity = capabilities.ValidationIdentity,
            FirstNumber = first.Number,
            LastNumber = first.Number,
            FirstHash = first.Hash,
            LastHash = first.Hash,
        };
        List<ProveChunk> attempted = [cold[0], new ProveChunk { CachedRange = missing }, .. cold.Skip(2)];
        RpcException unavailable = (await Assert.ThrowsAsync<RpcException>(() => Prove(client, attempted)))!;
        Assert.That(unavailable.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
        Assert.That(ProveFailure.Parser.ParseFrom(unavailable.Trailers.GetValueBytes("grpc-status-details-bin")).CacheMiss.BlockHash, Is.EqualTo(first.Hash));
    }

    [Test]
    public async Task PrepareOrdinary_TerminalSystemBlock_Rejects()
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-static.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement);
        using GrpcChannel channel = server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());
        List<ProveChunk> cold = NativeWindow(document.RootElement, capabilities.ValidationIdentity);
        BlockWitness terminal = cold[^1].Block;
        Assert.That(Rlp.Decode<Block>(terminal.Rlp.ToByteArray())!.Transactions.Any(static transaction => transaction.IsEezSystemTransaction()), Is.True);
        PrepareHeader header = new()
        {
            RollupId = cold[0].Header.RollupId,
            ExpectedValidationIdentity = capabilities.ValidationIdentity,
            FromBlock = terminal.Number,
            ToBlock = terminal.Number,
        };

        RpcException failure = (await Assert.ThrowsAsync<RpcException>(() => Prepare(client,
            [new PrepareChunk { Header = header }, new PrepareChunk { Block = terminal }])))!;

        Assert.That(failure.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
    }

    [Test]
    public async Task ProveV1_CachedTerminal_RequiresFreshWitness()
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement);
        using GrpcChannel channel = server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());
        List<ProveChunk> cold = NativeWindow(document.RootElement, capabilities.ValidationIdentity);
        BlockWitness terminal = cold[^1].Block;
        cold[^1] = new ProveChunk
        {
            CachedRange = new CachedOrdinaryRange
            {
                ValidationIdentity = capabilities.ValidationIdentity,
                FirstNumber = terminal.Number,
                LastNumber = terminal.Number,
                FirstHash = terminal.Hash,
                LastHash = terminal.Hash,
            },
        };

        RpcException failure = (await Assert.ThrowsAsync<RpcException>(() => Prove(client, cold)))!;

        Assert.That(failure.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    private static async Task<PrepareResponse> PrepareWindow(ProverV1.ProverClient client, List<ProveChunk> window, ByteString identity)
    {
        PrepareResponse? aggregate = null;
        for (int offset = 1; offset < window.Count - 1; offset += 64)
        {
            List<ProveChunk> segment = [window[0], .. window.Skip(offset).Take(Math.Min(64, window.Count - 1 - offset)), window[^1]];
            List<PrepareChunk> chunks = Preparation(segment, identity);
            if (aggregate is not null) chunks[0].Header.LeaseId = aggregate.LeaseId;
            PrepareResponse response = await Prepare(client, chunks);
            if (aggregate is null) aggregate = response.Clone();
            else
            {
                Assert.That(response.LeaseId, Is.EqualTo(aggregate.LeaseId));
                aggregate.LastNumber = response.LastNumber;
                aggregate.LastHash = response.LastHash;
                aggregate.PreparedCount += response.PreparedCount;
            }
        }
        return aggregate!;
    }

    private static List<PrepareChunk> Preparation(List<ProveChunk> window, ByteString identity)
    {
        BlockWitness[] ordinary = window.Skip(1).SkipLast(1).Take(64).Select(static chunk => chunk.Block.Clone()).ToArray();
        Assert.That(ordinary, Is.Not.Empty);
        List<PrepareChunk> chunks = [new PrepareChunk
        {
            Header = new PrepareHeader
            {
                RollupId = window[0].Header.RollupId, ExpectedValidationIdentity = identity,
                FromBlock = ordinary[0].Number, ToBlock = ordinary[^1].Number,
            },
        }];
        chunks.AddRange(ordinary.Select(static block => new PrepareChunk { Block = block }));
        return chunks;
    }

    private static CachedOrdinaryRange PreparedRange(PrepareResponse prepared) => new()
    {
        ValidationIdentity = prepared.ValidationIdentity,
        FirstNumber = prepared.FirstNumber,
        FirstHash = prepared.FirstHash,
        LastNumber = prepared.LastNumber,
        LastHash = prepared.LastHash,
    };

    private static async Task<PrepareResponse> Prepare(ProverV1.ProverClient client, IEnumerable<PrepareChunk> chunks)
    {
        using AsyncClientStreamingCall<PrepareChunk, PrepareResponse> call = client.PrepareOrdinary(new CallOptions(deadline: DateTime.UtcNow.AddMinutes(2)));
        try
        {
            foreach (PrepareChunk chunk in chunks) await call.RequestStream.WriteAsync(chunk);
            await call.RequestStream.CompleteAsync();
        }
        catch (RpcException) { }
        catch (InvalidOperationException) { }
        return await call.ResponseAsync;
    }
}

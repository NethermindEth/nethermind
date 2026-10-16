// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using NUnit.Framework;
using ProverV1 = Nethermind.Eez.Prove.Prover;
using ProverV2 = Nethermind.Eez.Prove.V2.Prover;
using PostBatch = Nethermind.Eez.Execution.Settlement.PostBatch;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXStreamingConformanceTests
{
    private static string DirectoryPath => RollupXRecordedExecutionTests.SelectDirectory(
        Environment.GetEnvironmentVariable("EEZ_ROLLUPX_EXECUTION_VECTOR_DIR"), Environment.GetEnvironmentVariable("EEZ_REQUIRE_UPSTREAM_VECTORS") == "true");

    [TestCaseSource(nameof(Cases))]
    public async Task ProveStream_OriginalUpstreamTranscript_ReturnsExpectedFrames(string name)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, name);
        JsonElement scenario = document.RootElement;
        await using RollupXStreamingServer server = await RollupXStreamingServer.Start(DirectoryPath, scenario);
        using GrpcChannel channel = server.Channel();
        ProverV2.ProverClient client = new(channel);
        Address attester = new(scenario.GetProperty("oracle").GetProperty("attester").GetString()!);
        await Replay(client, scenario, attester);
        await server.Admission.WaitUntilIdle();
    }

    [Test]
    public async Task RollupXProfile_V1IsUnavailable([Values] bool tls)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.Start(DirectoryPath, document.RootElement);
        using GrpcChannel channel = tls ? server.Channel() : server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        using AsyncClientStreamingCall<ProveChunk, ProveResponse> call = client.Prove();
        try { await call.RequestStream.CompleteAsync(); }
        catch (RpcException) { }
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unimplemented), "the native profile must never sign a Rollup0 proof through v1");
    }

    [TestCase("reorg", StatusCode.Aborted)]
    [TestCase("lookup", StatusCode.FailedPrecondition)]
    [TestCase("anchor", StatusCode.FailedPrecondition)]
    public async Task Finalize_UntrustedEvidence_DoesNotReturnProof(string change, StatusCode code)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.Start(DirectoryPath, document.RootElement, change == "reorg");
        using GrpcChannel channel = server.Channel();
        Address attester = new(document.RootElement.GetProperty("oracle").GetProperty("attester").GetString()!);
        await Replay(new ProverV2.ProverClient(channel), document.RootElement, attester,
            change == "reorg" ? null : settlement => MutateSettlement(settlement, change), code);
        await server.Admission.WaitUntilIdle();
    }

    [TestCase("single-block-proof.json", StatusCode.FailedPrecondition, TestName = "Finalize_RollupXIncompleteSyncWindow_ReturnsNoProof")]
    [TestCase("terminal-before-tip.json", StatusCode.FailedPrecondition, TestName = "Finalize_RollupXNonSyncTerminal_ReturnsNoProof")]
    [TestCase("subrange-promoted-anchor.json", StatusCode.Unavailable, TestName = "Finalize_RollupXParentWithoutAcceptanceEvidence_ReturnsNoProof")]
    public async Task Finalize_OriginalRecordingWithoutRequiredSettlementAuthority_ReturnsNoProof(string name, StatusCode expected)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, name);
        await using RollupXStreamingServer server = await RollupXStreamingServer.Start(DirectoryPath, document.RootElement);
        using GrpcChannel channel = server.Channel();
        using AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call = new ProverV2.ProverClient(channel).ProveStream(new CallOptions(deadline: DateTime.UtcNow.AddMinutes(2)));
        ServerFrame? previous = null;
        foreach (JsonElement item in document.RootElement.GetProperty("events").EnumerateArray())
        {
            if (item.GetProperty("direction").GetString() != "client") continue;
            ClientFrame request = ClientFrame.Parser.ParseFrom(Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!));
            if (previous is not null) { request.SessionId = previous.SessionId; request.Epoch = previous.Epoch; }
            await call.RequestStream.WriteAsync(request);
            Assert.That(await call.ResponseStream.MoveNext(), Is.True, "the original request has a correlated response");
            ServerFrame response = call.ResponseStream.Current;
            if (request.Finalize is not null)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(response.Proof, Is.Null, "recorded expected signatures cannot replace required Sync and canonical-parent authority");
                    Assert.That(response.Rejected?.Code, Is.EqualTo((int)expected));
                    Assert.That(response.RequestId, Is.EqualTo(request.RequestId));
                    Assert.That(response.Rejected?.ValidatedThrough, Is.EqualTo(previous!.Validated.Number), "failed authorization preserves the executed prefix");
                }
                await call.RequestStream.CompleteAsync();
                Assert.That(await call.ResponseStream.MoveNext(), Is.False);
                await server.Admission.WaitUntilIdle();
                return;
            }
            Assert.That(response.Rejected, Is.Null, "the original prefix remains executable");
            previous = response;
        }
        Assert.Fail("the original recording has no finalization request");
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        using JsonDocument provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "provenance.json")));
        string[] cases = provenance.RootElement.GetProperty("cases").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        Assert.That(cases, Has.Length.EqualTo(29).And.Unique, "a changed upstream case inventory needs an explicit coverage review");
        foreach (string name in cases)
        {
            using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, name);
            Assert.That(document.RootElement.GetProperty("format").GetInt32(), Is.EqualTo(2), "format changes require an explicit replay adapter");
            Assert.That(document.RootElement.GetProperty("case").GetString() + ".json", Is.EqualTo(name), "every selected case retains its identity");
            yield return new TestCaseData(name).SetName("ProveStream_RollupX_Upstream_" + Path.GetFileNameWithoutExtension(name));
        }
    }

    private static async Task Replay(ProverV2.ProverClient client, JsonElement scenario, Address attester,
        Action<SettlementInput>? mutate = null, StatusCode? rejectedProof = null)
    {
        Dictionary<int, AsyncDuplexStreamingCall<ClientFrame, ServerFrame>> streams = [];
        Dictionary<ByteString, ByteString> sessions = [];
        Dictionary<(ByteString Id, ulong Epoch), ulong> epochs = [];
        Dictionary<int, StatusCode> terminal = [];
        int proofs = 0;
        int rejections = 0;
        int index = 0;
        AsyncDuplexStreamingCall<ClientFrame, ServerFrame> Open(int transport)
        {
            AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call = client.ProveStream(new CallOptions(deadline: DateTime.UtcNow.AddMinutes(2)));
            streams.Add(transport, call);
            return call;
        }
        try
        {
            foreach (JsonElement item in scenario.GetProperty("events").EnumerateArray())
            {
                int transport = item.GetProperty("transport").GetInt32();
                string? direction = item.GetProperty("direction").GetString();
                string context = $"{scenario.GetProperty("case").GetString()} event {index++}, transport {transport}, {direction}";
                switch (direction)
                {
                    case "client":
                        ClientFrame request = ClientFrame.Parser.ParseFrom(Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!));
                        if (request.Finalize is not null) mutate?.Invoke(request.Finalize.Settlement);
                        if (!request.SessionId.IsEmpty)
                        {
                            if (epochs.TryGetValue((request.SessionId, request.Epoch), out ulong epoch)) request.Epoch = epoch;
                            if (sessions.TryGetValue(request.SessionId, out ByteString? id)) request.SessionId = id;
                        }
                        if (!streams.TryGetValue(transport, out AsyncDuplexStreamingCall<ClientFrame, ServerFrame>? outbound)) outbound = Open(transport);
                        await outbound.RequestStream.WriteAsync(request);
                        break;
                    case "server":
                        ServerFrame expected = ServerFrame.Parser.ParseFrom(Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!));
                        Assert.That(await streams[transport].ResponseStream.MoveNext(), Is.True, context + ": a correlated response is required");
                        ServerFrame actual = streams[transport].ResponseStream.Current.Clone();
                        if (expected.Proof is not null && rejectedProof is { } rejection)
                        {
                            Assert.That(actual.Proof, Is.Null, context + ": untrusted evidence must not return a signature");
                            Assert.That(actual.Rejected, Is.Not.Null, context + ": the real finalizer must reject");
                            Assert.That((actual.RequestId, actual.Rejected.Code), Is.EqualTo((expected.RequestId, (int)rejection)), context);
                            rejections++;
                            await streams[transport].RequestStream.CompleteAsync();
                        }
                        else StreamingConformanceTests.CompareFrame(expected, actual, sessions, epochs, attester, context);
                        if (actual.Proof is not null) proofs++;
                        break;
                    case "half_close": await streams[transport].RequestStream.CompleteAsync(); break;
                    case "disconnect": streams[transport].Dispose(); streams.Remove(transport); break;
                    case "rpc_open_error":
                        AsyncDuplexStreamingCall<ClientFrame, ServerFrame> opening = Open(transport);
                        RpcException admission = (await Assert.ThrowsAsync<RpcException>(async () => await opening.ResponseStream.MoveNext()))!;
                        Assert.That((int)admission.StatusCode, Is.EqualTo(item.GetProperty("code").GetInt32()), context + ": RPC admission status is unchanged");
                        opening.Dispose();
                        streams.Remove(transport);
                        break;
                    case "rpc_error":
                        RpcException error = (await Assert.ThrowsAsync<RpcException>(async () => await streams[transport].ResponseStream.MoveNext()))!;
                        Assert.That((int)error.StatusCode, Is.EqualTo(item.GetProperty("code").GetInt32()), context + ": transport error code is unchanged");
                        terminal.Add(transport, error.StatusCode);
                        break;
                    case "eof":
                        if (terminal.Remove(transport, out StatusCode status))
                            Assert.That(streams[transport].GetStatus().StatusCode, Is.EqualTo(status), context + ": the observed error is terminal");
                        else Assert.That(await streams[transport].ResponseStream.MoveNext(), Is.False, context + ": no response may follow EOF");
                        streams[transport].Dispose();
                        streams.Remove(transport);
                        break;
                    default: throw new InvalidOperationException(context + ": unsupported event direction");
                }
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(streams, Is.Empty, "every recorded transport reaches EOF, disconnect or RPC admission failure");
                Assert.That(rejections, Is.EqualTo(rejectedProof is null ? 0 : scenario.GetProperty("expected_proofs").GetInt32()));
                Assert.That(proofs, Is.EqualTo(rejectedProof is null ? scenario.GetProperty("expected_proofs").GetInt32() : 0), "every original proof oracle is exercised");
            }
        }
        finally { foreach (AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call in streams.Values) call.Dispose(); }
    }

    private static void MutateSettlement(SettlementInput input, string change)
    {
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(input.AbiCalldata.Span);
        if (change == "anchor") batch.Entries[0] = batch.Entries[0] with { Success = false };
        else
        {
            byte[] logical = new byte[input.Blobs.Count * BlobStream.DataBytesPerBlob];
            for (int i = 0; i < input.Blobs.Count; i++)
                BlobStream.Unpack(input.Blobs[i].Span, logical.AsSpan(i * BlobStream.DataBytesPerBlob, BlobStream.DataBytesPerBlob));
            NativeStream stream = NativeStream.Decode(logical, 1);
            int index = Array.FindIndex(stream.Messages, static message => message is StreamMessage.Return);
            StreamMessage.Return result = (StreamMessage.Return)stream.Messages[index];
            Assert.That(result.Success, Is.False, "precondition: this is a non-applying lookup observation");
            stream.Messages[index] = result with { ReturnData = new byte[] { 0x45 } };
            batch.Entries[1] = batch.Entries[1] with { ReturnData = [0x45] };
            input.Blobs.Clear();
            input.Blobs.AddRange(BlobStream.Pack(NativeStream.Encode(1, stream.Operations.Span, stream.Messages)).Select(static blob => ByteString.CopyFrom(blob)));
        }
        input.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch));
    }
}

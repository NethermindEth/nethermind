// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove.V2;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class StreamingConformanceTests
{
    private static string DirectoryPath(bool prooflessRequests)
    {
        if (prooflessRequests) return StatelessFixtures.PathOf("streaming-conformance", "");
        string? path = Environment.GetEnvironmentVariable("EEZ_CONFORMANCE_VECTOR_DIR");
        if (!string.IsNullOrEmpty(path)) return path;
        if (Environment.GetEnvironmentVariable("EEZ_REQUIRE_UPSTREAM_VECTORS") == "true")
            throw new InvalidOperationException("the streaming CI job requires its selected upstream vector directory");
        return StatelessFixtures.PathOf("streaming-conformance", "rollup0-conditional");
    }

    private static IEnumerable<TestCaseData> Cases() => ReadCases(false);

    private static IEnumerable<TestCaseData> ReadCases(bool prooflessRequests)
    {
        foreach (string file in new[] { "rollup0-transcripts.json", "rollup0-limits-transcripts.json" })
        {
            JsonElement document = Read(file, prooflessRequests);
            foreach (JsonElement item in document.GetProperty("cases").EnumerateArray())
            {
                string name = item.GetProperty("name").GetString()!;
                yield return new TestCaseData(file, name).SetName($"ProveStream_Upstream_{name}");
            }
        }
    }

    private static IEnumerable<TestCaseData> ProoflessCases()
    {
        foreach (TestCaseData test in ReadCases(true))
        {
            string file = (string)test.Arguments[0]!;
            string name = (string)test.Arguments[1]!;
            yield return new TestCaseData(file, name).SetName($"ProveStream_ProoflessRequests_{name}");
        }
    }

    [TestCaseSource(nameof(Cases))]
    public async Task ProveStream_UpstreamTranscript_ReturnsExpectedFrames(string file, string name) =>
        await ReplayScenario(file, name, prooflessRequests: false);

    [TestCaseSource(nameof(ProoflessCases))]
    public async Task ProveStream_ProoflessRequests_ReturnExpectedFrames(string file, string name) =>
        await ReplayScenario(file, name, prooflessRequests: true);

    private static async Task ReplayScenario(string file, string name, bool prooflessRequests)
    {
        JsonElement document = Read(file, prooflessRequests);
        JsonElement scenario = document.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        ulong parent = name switch
        {
            "finalize-subrange" => 80,
            "finalize-last-block" => 83,
            "block-quota" or "payload-quota" or "witness-quota" => 79,
            _ => 78,
        };
        await using StreamingEndpointTests.EndpointServer server = await StreamingEndpointTests.EndpointServer.Start(
            StatelessFixtures.Window84, 79, 84, Limits(scenario), parent, profile: prooflessRequests ? StreamingSettlementProfile.Rollup0Calldata : StreamingSettlementProfile.Rollup0Conditional);
        using GrpcChannel channel = server.Channel(server.ClientA);
        Prover.ProverClient client = new(channel);
        Address attester = new(document.GetProperty("oracle").GetProperty("attester").GetString()!);
        await Replay(client, scenario, attester, prooflessRequests);
        await server.Admission.WaitUntilIdle();
    }

    [TestCase("finalize-subrange", TestName = "ProveStream_UnsettledSubrange_RejectsProof")]
    [TestCase("finalize-last-block", TestName = "ProveStream_UnsettledLastBlock_RejectsProof")]
    public async Task ProveStream_ParentNotRegistered_RejectsBeforeSigning(string name)
    {
        JsonElement scenario = Read("rollup0-transcripts.json", true).GetProperty("cases").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);
        await using StreamingEndpointTests.EndpointServer server = await StreamingEndpointTests.EndpointServer.Start(
            StatelessFixtures.Window84, 79, 84, new WindowLimits(8, 16 * 1024 * 1024, 100_000), 78);
        using GrpcChannel channel = server.Channel(server.ClientA);
        using AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call = new Prover.ProverClient(channel).ProveStream(Options());
        ServerFrame? current = null;
        foreach (JsonElement step in scenario.GetProperty("steps").EnumerateArray())
        {
            if (step.GetProperty("action").GetString() != "send") continue;
            ClientFrame frame = ClientFrame.Parser.ParseFrom(Convert.FromHexString(step.GetProperty("protobuf_hex").GetString()!));
            if (current is not null) { frame.SessionId = current.SessionId; frame.Epoch = current.Epoch; }
            if (frame.Finalize is not null)
            {
                PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(frame.Finalize.Settlement.AbiCalldata.Span);
                frame.Finalize.Settlement.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch with { Proofs = [[]] }));
            }
            await call.RequestStream.WriteAsync(frame);
            Assert.That(await call.ResponseStream.MoveNext(), Is.True, "every request returns a correlated response");
            current = call.ResponseStream.Current;
            if (frame.Finalize is not null)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(current.Proof, Is.Null, "a fixture transition does not establish L1 acceptance of its selected predecessor");
                    Assert.That(current.Rejected?.Code, Is.EqualTo((int)StatusCode.FailedPrecondition), "production canonical authorization remains enabled");
                    Assert.That(current.RequestId, Is.EqualTo(frame.RequestId), "authorization rejection preserves request correlation");
                }
                await call.RequestStream.CompleteAsync();
                Assert.That(await call.ResponseStream.MoveNext(), Is.False, "rejected finalization does not produce a later proof");
                return;
            }
            Assert.That(current.Rejected, Is.Null, "the recorded prefix executes before its selected parent is checked");
        }
        Assert.Fail("the selected upstream scenario has no Finalize");
    }

    private static JsonElement Read(string file, bool prooflessRequests)
    {
        string path = Path.Combine(DirectoryPath(prooflessRequests), file);
        Assert.That(new FileInfo(path).Length, Is.InRange(1L, 8L * 1024 * 1024), "vector data must have a bounded size");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.GetProperty("format").GetString(), Is.EqualTo("eez-prove-v2-transcript-1"), "unknown transcript formats require an explicit adapter");
            Assert.That(document.RootElement.GetProperty("cases").GetArrayLength(), Is.InRange(1, 256), "an empty vector file is not conformance evidence");
        }
        HashSet<string> names = [];
        foreach (JsonElement scenario in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            string? name = scenario.GetProperty("name").GetString();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(string.IsNullOrEmpty(name), Is.False, "every upstream case has an explicit name");
                Assert.That(names.Add(name!), Is.True, "duplicate case names cannot hide another transcript");
            }
            JsonElement steps = scenario.GetProperty("steps");
            Assert.That(steps.GetArrayLength(), Is.InRange(4, 4096), "every case contains a bounded request and response flow");
            string?[] actions = steps.EnumerateArray().Select(step => step.GetProperty("action").GetString()).ToArray();
            foreach (string required in new[] { "open", "send", "receive", "eof" })
                Assert.That(actions, Does.Contain(required), "empty or unobserved streams are not conformance evidence");
        }
        return document.RootElement.Clone();
    }

    private static WindowLimits Limits(JsonElement scenario)
    {
        WindowLimits limits = new(8, 16 * 1024 * 1024, 100_000);
        if (!scenario.TryGetProperty("server_args", out JsonElement arguments)) return limits;
        Assert.That(arguments.GetArrayLength(), Is.EqualTo(2), "configured cases declare one supported quota");
        long value = long.Parse(arguments[1].GetString()!, CultureInfo.InvariantCulture);
        return arguments[0].GetString() switch
        {
            "--max-request-blocks" => limits with { MaxBlocks = checked((int)value) },
            "--max-request-bytes" => limits with { MaxBytes = value },
            "--max-request-witness-items" => limits with { MaxWitnessItems = value },
            _ => throw new InvalidOperationException("unsupported upstream server quota"),
        };
    }

    private static byte[]? ProoflessCalldata(ByteString calldata)
    {
        PostBatch batch;
        try { batch = EezCalldata.DecodePostAndVerifyBatch(calldata.Span); }
        catch (EezAbiException) { return null; }
        byte[][] proofs = new byte[batch.Proofs.Length][];
        Array.Fill(proofs, Array.Empty<byte>());
        return EezCalldata.EncodePostAndVerifyBatch(batch with { Proofs = proofs });
    }

    private static async Task Replay(Prover.ProverClient client, JsonElement scenario, Address attester, bool prooflessRequests)
    {
        Dictionary<string, AsyncDuplexStreamingCall<ClientFrame, ServerFrame>> streams = [];
        Dictionary<ByteString, ByteString> sessions = [];
        Dictionary<(ByteString Id, ulong Epoch), ulong> epochs = [];
        Dictionary<string, StatusCode> terminalStatuses = [];
        int index = 0;
        try
        {
            foreach (JsonElement step in scenario.GetProperty("steps").EnumerateArray())
            {
                string name = step.GetProperty("stream").GetString()!;
                string context = $"{scenario.GetProperty("name").GetString()} step {index++}, {name}, {step.GetProperty("action").GetString()}";
                switch (step.GetProperty("action").GetString())
                {
                    case "open":
                        streams.Add(name, client.ProveStream(Options()));
                        break;
                    case "send":
                        ClientFrame request = ClientFrame.Parser.ParseFrom(Convert.FromHexString(step.GetProperty("protobuf_hex").GetString()!));
                        if (!request.SessionId.IsEmpty)
                        {
                            if (epochs.TryGetValue((request.SessionId, request.Epoch), out ulong epoch)) request.Epoch = epoch;
                            if (sessions.TryGetValue(request.SessionId, out ByteString? id)) request.SessionId = id;
                        }
                        if (prooflessRequests && request.Finalize?.Settlement is { } settlement && ProoflessCalldata(settlement.AbiCalldata) is { } calldata)
                            settlement.AbiCalldata = ByteString.CopyFrom(calldata);
                        await streams[name].RequestStream.WriteAsync(request);
                        break;
                    case "receive":
                        ServerFrame expected = ServerFrame.Parser.ParseFrom(Convert.FromHexString(step.GetProperty("protobuf_hex").GetString()!));
                        Assert.That(await streams[name].ResponseStream.MoveNext(), Is.True, context + ": expected response, not EOF");
                        ServerFrame actual = streams[name].ResponseStream.Current.Clone();
                        CompareFrame(expected, actual, sessions, epochs, attester, context);
                        break;
                    case "half_close":
                        await streams[name].RequestStream.CompleteAsync();
                        break;
                    case "rpc_error":
                        RpcException failure = (await Assert.ThrowsAsync<RpcException>(async () => await streams[name].ResponseStream.MoveNext()))!;
                        Assert.That((int)failure.StatusCode, Is.EqualTo(step.GetProperty("code").GetInt32()), context + ": terminal gRPC status remains an assertion");
                        terminalStatuses.Add(name, failure.StatusCode);
                        break;
                    case "eof":
                        if (terminalStatuses.Remove(name, out StatusCode terminal))
                            Assert.That(streams[name].GetStatus().StatusCode, Is.EqualTo(terminal), context + ": the observed RPC error is terminal");
                        else
                            Assert.That(await streams[name].ResponseStream.MoveNext(), Is.False, context + ": server EOF does not require an implicit client half-close");
                        streams[name].Dispose();
                        streams.Remove(name);
                        break;
                    default:
                        Assert.Fail(context + ": unsupported transcript action");
                        break;
                }
            }
            Assert.That(streams, Is.Empty, "all transcript streams reach an explicit EOF");
        }
        finally { foreach (AsyncDuplexStreamingCall<ClientFrame, ServerFrame> stream in streams.Values) stream.Dispose(); }
    }

    internal static void CompareFrame(ServerFrame expected, ServerFrame actual, Dictionary<ByteString, ByteString> sessions,
        Dictionary<(ByteString Id, ulong Epoch), ulong> epochs, Address attester, string context)
    {
        string diagnostic = actual.Rejected?.Message ?? "";
        NormalizeBindings(expected, actual, sessions, epochs, context);
        if (actual.Validated is not null) actual.Validated.Reused = false;
        if (actual.Rejected is not null) actual.Rejected.Message = "";
        if (actual.Proof is not null)
        {
            Assert.That(expected.Proof, Is.Not.Null, context + ": no unsolicited proof");
            byte[] signature = actual.Proof.Proof_.ToByteArray();
            Assert.That(signature.Length, Is.EqualTo(65), context + ": a proof is a recoverable ECDSA signature");
            Assert.That(signature[64], Is.AnyOf(27, 28), context + ": recovery byte follows the on-chain format");
            ValueHash256 digest = new(expected.Proof.PublicInputsHash.Span);
            Signature recoverable = new(signature.AsSpan(0, 64), signature[64] - 27);
            Assert.That(new EthereumEcdsa(0).RecoverAddress(recoverable, in digest), Is.EqualTo(attester), context + ": signer is recovered over the independent expected digest");
            actual.Proof.Proof_ = expected.Proof.Proof_;
        }
        Assert.That(actual, Is.EqualTo(expected), context + ": response fields and cursor match unchanged upstream expectations; " + diagnostic);
    }

    private static void NormalizeBindings(ServerFrame expected, ServerFrame actual, Dictionary<ByteString, ByteString> sessions,
        Dictionary<(ByteString Id, ulong Epoch), ulong> epochs, string context)
    {
        if (actual.Ready is not null)
        {
            Assert.That((actual.SessionId.Length, actual.Epoch != 0), Is.EqualTo((32, true)), context + ": Ready contains a valid server binding");
            if (sessions.TryGetValue(expected.SessionId, out ByteString? existing))
                Assert.That(actual.SessionId, Is.EqualTo(existing), context + ": recovery preserves session identity");
            else
            {
                Assert.That(sessions.Values, Does.Not.Contain(actual.SessionId), context + ": another session cannot reuse the same binding");
                sessions.Add(expected.SessionId, actual.SessionId);
            }
            (ByteString, ulong) key = (expected.SessionId, expected.Epoch);
            if (epochs.TryGetValue(key, out ulong epoch))
                Assert.That(actual.Epoch, Is.EqualTo(epoch), context + ": a known attachment keeps its epoch");
            else
            {
                Assert.That(epochs.Where(entry => entry.Key.Id == expected.SessionId).Select(entry => entry.Value),
                    Does.Not.Contain(actual.Epoch), context + ": a new attachment fences earlier epochs");
                epochs.Add(key, actual.Epoch);
            }
        }
        if (expected.SessionId.IsEmpty) return;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sessions.TryGetValue(expected.SessionId, out ByteString? id), Is.True, context + ": response has a known session");
            Assert.That(actual.SessionId, Is.EqualTo(id), context + ": response cannot cross sessions");
            Assert.That(epochs.TryGetValue((expected.SessionId, expected.Epoch), out ulong mapped), Is.True, context + ": response has a known epoch");
            Assert.That(actual.Epoch, Is.EqualTo(mapped), context + ": response cannot cross epochs");
        }
        actual.SessionId = expected.SessionId;
        actual.Epoch = expected.Epoch;
    }

    private static CallOptions Options() => new(deadline: DateTime.UtcNow.AddMinutes(2));
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using NUnit.Framework;
using ProverV1 = Nethermind.Eez.Prove.Prover;
using ProverV2 = Nethermind.Eez.Prove.V2.Prover;
using PostBatch = Nethermind.Eez.Execution.Settlement.PostBatch;

namespace Nethermind.Eez.Test.RollupX;

[NonParallelizable]
public partial class RollupXStreamingConformanceTests
{
    private static string LegacyDirectoryPath => StatelessFixtures.PathOf("rollupx/recorded-execution", "");

    private static string DirectoryPath => RollupXRecordedExecutionTests.SelectDirectory(
        Environment.GetEnvironmentVariable("EEZ_ROLLUPX_EXECUTION_VECTOR_DIR"), Environment.GetEnvironmentVariable("EEZ_REQUIRE_UPSTREAM_VECTORS") == "true");

    [TestCaseSource(nameof(Cases))]
    public async Task ProveStream_OriginalUpstreamTranscript_ReturnsExpectedFrames(string name)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, name);
        JsonElement scenario = document.RootElement;
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartFixture(DirectoryPath, scenario);
        using GrpcChannel channel = server.Channel();
        ProverV2.ProverClient client = new(channel);
        Address attester = new(scenario.GetProperty("oracle").GetProperty("attester").GetString()!);
        if (server.IsOffline)
        {
            Nethermind.Eez.Prove.V2.CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new Nethermind.Eez.Prove.V2.CapabilitiesRequest());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(capabilities.ProfileId, Is.EqualTo(2));
                Assert.That(capabilities.ValidationIdentity, Is.EqualTo(server.ExpectedValidationIdentity),
                    "published identity must bind the exact selected fixture deployment and independently encoded V4 preimage");
                Assert.That(capabilities.MaxWindowBlocks, Is.EqualTo(scenario.GetProperty("server_profile").GetProperty("max_window_blocks").GetUInt64()));
            }
        }
        await Replay(client, scenario, attester);
        await server.Admission.WaitUntilIdle();
    }

    [TestCase("baseline-lookup.json", TestName = "ProveStream_CanonicalLookupSources_ReturnsRecordedProof")]
    [TestCase("baseline-static.json", TestName = "ProveStream_CanonicalStaticSources_ReturnsRecordedProof")]
    public async Task ProveStream_CanonicalFixtureSources_ReturnsRecordedProof(string name)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, name);
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(DirectoryPath, document.RootElement);
        using GrpcChannel channel = server.Channel();
        Address attester = new(document.RootElement.GetProperty("oracle").GetProperty("attester").GetString()!);
        await Replay(new ProverV2.ProverClient(channel), document.RootElement, attester);
        await server.Admission.WaitUntilIdle();
    }

    [Test]
    public async Task RollupXProfile_V1EmptyStream_UsesItsOwnListener([Values] bool tls)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement);
        using GrpcChannel channel = tls ? server.Channel() : server.PlainChannel();
        ProverV1.ProverClient client = new(channel);
        using AsyncClientStreamingCall<ProveChunk, ProveResponse> call = client.Prove();
        try { await call.RequestStream.CompleteAsync(); }
        catch (RpcException) { }
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync))!;
        Assert.That(exception.StatusCode, Is.EqualTo(tls ? StatusCode.Unimplemented : StatusCode.InvalidArgument), "native v1 uses loopback; the v2 TLS listener never exposes it");
    }

    [TestCase("reorg", StatusCode.Aborted, false, TestName = "Finalize_CanonicalReorg_DoesNotReturnProof")]
    [TestCase("lookup", StatusCode.FailedPrecondition, false, TestName = "Finalize_CanonicalFalseLookup_DoesNotReturnProof")]
    [TestCase("anchor", StatusCode.FailedPrecondition, false, TestName = "Finalize_CanonicalFalseSettlementAnchor_DoesNotReturnProof")]
    [TestCase("lookup", StatusCode.FailedPrecondition, true, TestName = "Finalize_SyntheticFalseLookup_DoesNotReturnProof")]
    [TestCase("anchor", StatusCode.FailedPrecondition, true, TestName = "Finalize_SyntheticFalseSettlementAnchor_DoesNotReturnProof")]
    public async Task Finalize_UntrustedEvidence_DoesNotReturnProof(string change, StatusCode code, bool syntheticAuthority)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        await using RollupXStreamingServer server = syntheticAuthority
            ? await RollupXStreamingServer.StartFixture(LegacyDirectoryPath, document.RootElement)
            : await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement, change == "reorg");
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
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, name);
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(LegacyDirectoryPath, document.RootElement);
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

    [TestCase("number", TestName = "FixtureParent_ChangedNumber_IsRejected")]
    [TestCase("hash", TestName = "FixtureParent_UnknownHash_IsRejected")]
    [TestCase("state", TestName = "FixtureParent_ChangedStateRoot_IsRejected")]
    [TestCase("rollup", TestName = "FixtureParent_ChangedRollup_IsRejected")]
    public void FixtureParent_ChangedClaim_IsRejected(string change)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        Dictionary<Hash256, BlockHeader> headers = new() { [parent.Hash!] = parent };
        Begin anchor = new() { RollupId = 1, AnchorHash = ByteString.CopyFrom(parent.Hash!.Bytes), AnchorStateRoot = ByteString.CopyFrom(parent.StateRoot!.Bytes) };
        Assert.That(RollupXStreamingServer.SelectFixtureParent(headers, 1, anchor), Is.SameAs(parent), "precondition: the unchanged fixed fixture parent is accepted");
        switch (change)
        {
            case "number": anchor.AnchorNumber = 1; break;
            case "hash": anchor.AnchorHash = ByteString.CopyFrom(TestItem.KeccakA.Bytes); break;
            case "state": anchor.AnchorStateRoot = ByteString.CopyFrom(TestItem.KeccakA.Bytes); break;
            case "rollup": anchor.RollupId = 2; break;
        }
        RpcException exception = Assert.Throws<RpcException>(() => RollupXStreamingServer.SelectFixtureParent(headers, 1, anchor))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition), "caller claims cannot replace fixed fixture authority");
    }

    [TestCase(false, TestName = "CompareFrame_ChangedProofDigest_IsRejected")]
    [TestCase(true, TestName = "CompareFrame_WrongProofSigner_IsRejected")]
    public void CompareFrame_ChangedIndependentProofOracle_IsRejected(bool wrongSigner)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "baseline-lookup.json");
        ServerFrame expected = RecordedProof(document.RootElement);
        Address attester = new(document.RootElement.GetProperty("oracle").GetProperty("attester").GetString()!);
        Dictionary<ByteString, ByteString> sessions = new() { [expected.SessionId] = expected.SessionId };
        Dictionary<(ByteString, ulong), ulong> epochs = new() { [(expected.SessionId, expected.Epoch)] = expected.Epoch };
        Assert.That(() => StreamingConformanceTests.CompareFrame(expected, expected.Clone(), sessions, epochs, attester, "recorded proof"),
            Throws.Nothing, "precondition: the captured proof satisfies its independent digest and signer oracle");
        ServerFrame actual = expected.Clone();
        if (wrongSigner) attester = TestItem.AddressA;
        else actual.Proof.PublicInputsHash = ByteString.CopyFrom(TestItem.KeccakA.Bytes);
        Assert.That(() => StreamingConformanceTests.CompareFrame(expected, actual, sessions, epochs, attester, "corrupted proof"),
            Throws.TypeOf<AssertionException>(), "the comparator cannot learn expected proof output from the server");
    }

    [Test]
    public async Task SettlementExecution_ChangedTrustedSeed_RejectsRecordedWindow()
    {
        await KzgPolynomialCommitments.InitializeAsync();
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, "single-block-proof.json");
        string fixture = document.RootElement.GetProperty("fixture").GetString()!;
        using JsonDocument genesis = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, fixture + ".genesis.json");
        using JsonDocument l1Genesis = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, fixture + ".l1-genesis.json");
        using JsonDocument identity = RollupXRecordedExecutionTests.ReadSelected(LegacyDirectoryPath, fixture + ".identity.json");
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string l2Path = Path.Combine(directory.Path, "l2.json"), l1Path = Path.Combine(directory.Path, "l1.json");
        File.WriteAllText(l2Path, genesis.RootElement.GetRawText());
        File.WriteAllText(l1Path, l1Genesis.RootElement.GetRawText());
        (ISpecProvider specs, EezChainRules rules) = ChainConfigLoader.LoadProfile(l2Path);
        EezSettlementContext context = new(identity.RootElement.GetProperty("rollup_id").GetUInt64(), specs.ChainId,
            new Address(identity.RootElement.GetProperty("proof_system").GetString()!),
            new ValueHash256(identity.RootElement.GetProperty("vkey").GetString()!), rules.Schedule!.BlockIntervalSeconds);
        ClientFrame[] requests = document.RootElement.GetProperty("events").EnumerateArray()
            .Where(static item => item.GetProperty("direction").GetString() == "client")
            .Select(static item => ClientFrame.Parser.ParseFrom(Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!))).ToArray();
        Begin anchor = requests.Single(static frame => frame.Begin is not null).Begin;
        BlockWitness block = requests.Single(static frame => frame.Block is not null).Block.Data;
        Finalize selection = requests.Single(static frame => frame.Finalize is not null).Finalize;
        BlockHeader parent = block.Witness.Headers.Select(static bytes => Rlp.Decode<BlockHeader>(bytes.ToByteArray())!)
            .Single(header => header.Hash!.Bytes.SequenceEqual(anchor.AnchorHash.Span));
        RollupXSettlementInput input = RollupXSettlementInput.Decode(selection.Settlement, context, ChainConfigLoader.LoadL1(l1Path), CancellationToken.None);
        using AdmittedWindow window = new(selection.FromBlock, selection.ToBlock, selection.Settlement.AbiCalldata.ToByteArray(),
            [new EezStatelessBlock(block.Rlp.ToByteArray(), WindowAssembler.ToWitness(block.Witness))],
            [(new Hash256(block.Hash.Span), new Hash256(block.ParentHash.Span))]);
        RollupXSettlementExecution execution = new(specs, rules, context, LimboLogs.Instance);
        Hash256 seed = parent.MixHash!;
        Assert.That(execution.Validate(window, input, parent, seed, parent.Hash!, CancellationToken.None), Is.EqualTo(new ValueHash256(RecordedProof(document.RootElement).Proof.PublicInputsHash.Span)),
            "precondition: the original seed reproduces the independent one-block digest");
        Assert.That(seed, Is.Not.EqualTo(TestItem.KeccakA), "precondition: the mutation changes the trusted descendant seed");
        Assert.That(() => execution.Validate(window, input, parent, TestItem.KeccakA, parent.Hash!, CancellationToken.None), Throws.TypeOf<EezSettlementException>(),
            "valid execution cannot be signed against a different trusted descendant seed");
    }

    private static ServerFrame RecordedProof(JsonElement scenario) => scenario.GetProperty("events").EnumerateArray()
        .Where(static item => item.GetProperty("direction").GetString() == "server")
        .Select(static item => ServerFrame.Parser.ParseFrom(Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!)))
        .Single(static frame => frame.Proof is not null);

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

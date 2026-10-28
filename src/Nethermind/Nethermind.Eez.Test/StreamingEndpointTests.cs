// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using ProverV1 = Nethermind.Eez.Prove.Prover;
using ProverV2 = Nethermind.Eez.Prove.V2.Prover;
using StreamBlock = Nethermind.Eez.Prove.V2.Block;
using SettlementBatch = Nethermind.Eez.Execution.Settlement.PostBatch;

namespace Nethermind.Eez.Test;

[TestFixture(StatelessFixtures.Window84, 79, 84)]
[TestFixture("captured-devnet-window-468", 463, 468)]
public class StreamingEndpointTests(string fixture, int from, int to)
{
    private EndpointServer _server = null!;

    [SetUp]
    public async Task SetUp() => _server = await EndpointServer.Start(fixture, from, to);

    [TearDown]
    public async Task TearDown() => await _server.DisposeAsync();

    [TestCase(false, TestName = "ProveStream_AuthenticatedProfileOverRealTls_ReturnsRecordedProofAndEof")]
    [TestCase(true, TestName = "ProveStream_ConditionalProfileOverRealTls_ReturnsRecordedProofAndEof")]
    public async Task ProveStream_PinnedComposerOverRealTls_ReturnsRecordedProofAndEof(bool conditional)
    {
        if (conditional)
        {
            await _server.DisposeAsync();
            _server = await EndpointServer.Start(fixture, from, to, profile: StreamingSettlementProfile.Rollup0Conditional);
        }
        using GrpcChannel channel = _server.Channel(_server.ClientA);
        ProverV2.ProverClient client = new(channel);
        using AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call = client.ProveStream(Options());
        ServerFrame current = await Begin(call);
        foreach (ProveChunk chunk in _server.Chunks.Skip(1))
        {
            await call.RequestStream.WriteAsync(new ClientFrame
            {
                SessionId = current.SessionId,
                Epoch = current.Epoch,
                RequestId = chunk.Block.Number,
                Block = new StreamBlock { Data = chunk.Block }
            });
            Assert.That(await call.ResponseStream.MoveNext(), Is.True, "each accepted block has a correlated response");
            current = call.ResponseStream.Current;
            Assert.That(current.Validated?.Hash, Is.EqualTo(chunk.Block.Hash), "production DI resolves the actual block executor");
        }
        await call.RequestStream.WriteAsync(new ClientFrame
        {
            SessionId = current.SessionId,
            Epoch = current.Epoch,
            RequestId = 999,
            Finalize = new Finalize
            {
                FromBlock = (ulong)from,
                ToBlock = (ulong)to,
                TerminalHash = _server.Chunks[^1].Block.Hash,
                Settlement = new SettlementInput { AbiCalldata = _server.Chunks[0].Header.PostBatch.AbiCalldata }
            }
        });
        await call.RequestStream.CompleteAsync();
        Assert.That(await call.ResponseStream.MoveNext(), Is.True, "accepted Finalize drains through a client half-close");
        Proof proof = call.ResponseStream.Current.Proof;
        Assert.That(proof, Is.Not.Null, "production endpoint must execute, authorize and sign, not return a placeholder");
        JsonElement oracle = StatelessFixtures.ReadJson(fixture, "oracle.json");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof.PublicInputsHash.ToByteArray().ToHexString(true), Is.EqualTo(oracle.GetProperty("public_inputs_hash").GetString()), "independent public-input oracle");
            Assert.That(proof.Proof_.ToByteArray().ToHexString(true), Is.EqualTo((oracle.TryGetProperty("mined_proof", out JsonElement mined) ? mined : oracle.GetProperty("expected_test_signature")).GetString()), "independent recorded signature");
            Assert.That(call.ResponseStream.Current.RequestId, Is.EqualTo(999), "Proof preserves Finalize correlation");
            Assert.That(_server.L1.SnapshotReads, Is.EqualTo(conditional ? 0 : 3), "real configured source RPCs authenticate the parent and recheck before signing");
        }
        Assert.That(await call.ResponseStream.MoveNext(), Is.False, "Proof ends the server stream");
        await _server.Admission.WaitUntilIdle();
    }

    [TestCase(false, TestName = "ProveStream_NoClientCertificate_RejectsTls")]
    [TestCase(true, TestName = "ProveStream_UnpinnedClientCertificate_RejectsTls")]
    public async Task ProveStream_UntrustedClient_DoesNotReachTheBackend(bool unpinned)
    {
        using X509Certificate2 other = EndpointServer.Certificate("unknown", false);
        using GrpcChannel channel = _server.Channel(unpinned ? other : null);
        using AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call = new ProverV2.ProverClient(channel).ProveStream(Options());
        Task<bool> response = call.ResponseStream.MoveNext();
        try { await call.RequestStream.WriteAsync(new ClientFrame { Begin = _server.Anchor }); }
        catch (Exception exception) when (exception is IOException or RpcException) { }
        RpcException failure = (await Assert.ThrowsAsync<RpcException>(async () => await response))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsTransportRejection(failure), Is.True, $"TLS requires a pinned certificate and its private key: {failure}");
            Assert.That(_server.L1.Requests, Is.EqualTo(0), "an untrusted connection never acquires settlement evidence");
        }
    }

    [Test]
    public void TransportRejection_InternalStatus_RequiresProtocolFailure([Values] bool transport)
    {
        Exception? cause = transport
            ? new HttpRequestException("HTTP/2 connection failed", new HttpProtocolException(0, "peer closed connection", null))
            : null;
        RpcException failure = new(new Status(StatusCode.Internal, "request failed", cause));
        Assert.That(IsTransportRejection(failure), Is.EqualTo(transport), "an application Internal status cannot stand in for transport rejection");
    }

    [Test]
    public async Task ProveStream_PlaintextEvenWithOwnerMetadata_IsUnauthenticated()
    {
        using GrpcChannel channel = GrpcChannel.ForAddress(_server.PlainAddress);
        using AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call = new ProverV2.ProverClient(channel).ProveStream(Options(new Metadata
        {
            { "owner", _server.ClientA.GetCertHashString(HashAlgorithmName.SHA256) },
            { "x-forwarded-proto", "https" },
        }));
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(new ClientFrame { Begin = _server.Anchor });
            await call.ResponseStream.MoveNext();
        }))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unauthenticated), "metadata cannot manufacture TLS peer authentication");
    }

    [TestCase(false, TestName = "Prove_V1OnTls_IsRejected")]
    [TestCase(true, TestName = "Prove_V1OnTlsWithForgedHostAndScheme_IsRejected")]
    public async Task Prove_V1CannotBypassAuthorizationOnTls(bool forged)
    {
        using GrpcChannel channel = _server.Channel(_server.ClientA, forged ? _server.PlainAddress.Authority : null);
        ProverV1.ProverClient client = new(channel);
        using AsyncClientStreamingCall<ProveChunk, ProveResponse> call = client.Prove(Options(forged ? new Metadata { { "x-forwarded-proto", "http" } } : null));
        try
        {
            foreach (ProveChunk chunk in _server.Chunks) await call.RequestStream.WriteAsync(chunk);
            await call.RequestStream.CompleteAsync();
        }
        catch (RpcException) { }
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unimplemented), "the actual TLS connection cannot route to the unsigned-authority v1 service");
    }

    [Test]
    public async Task Resume_RealTlsCertificateOwner_PreservesProgressAndRejectsAnotherPinnedOwner()
    {
        using GrpcChannel channelA = _server.Channel(_server.ClientA);
        ProverV2.ProverClient clientA = new(channelA);
        ServerFrame ready;
        using (AsyncDuplexStreamingCall<ClientFrame, ServerFrame> first = clientA.ProveStream(Options()))
        {
            ready = await Begin(first);
            BlockWitness block = _server.Chunks[1].Block;
            await first.RequestStream.WriteAsync(new ClientFrame { SessionId = ready.SessionId, Epoch = ready.Epoch, RequestId = 2, Block = new StreamBlock { Data = block } });
            Assert.That(await first.ResponseStream.MoveNext(), Is.True, "first owner validates a real block before detaching");
            ready = first.ResponseStream.Current;
            Assert.That(ready.Validated?.Hash, Is.EqualTo(block.Hash), "retained progress comes from execution");
            await first.RequestStream.CompleteAsync();
            Assert.That(await first.ResponseStream.MoveNext(), Is.False, "half-close detaches without canceling the retained session");
        }
        using GrpcChannel channelB = _server.Channel(_server.ClientB);
        using (AsyncDuplexStreamingCall<ClientFrame, ServerFrame> other = new ProverV2.ProverClient(channelB).ProveStream(Options(new Metadata
            { { "owner", _server.ClientA.GetCertHashString(HashAlgorithmName.SHA256) } })))
        {
            await other.RequestStream.WriteAsync(new ClientFrame { SessionId = ready.SessionId, RequestId = 3, Resume = new Resume() });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await other.ResponseStream.MoveNext(), Is.True, "foreign owner receives a correlated refusal");
                Assert.That(other.ResponseStream.Current.Rejected?.Code, Is.EqualTo((int)StatusCode.NotFound), "another pinned certificate cannot take the first owner's session");
            }
            await other.RequestStream.CompleteAsync();
            Assert.That(await other.ResponseStream.MoveNext(), Is.False, "foreign resume does not attach");
        }
        using AsyncDuplexStreamingCall<ClientFrame, ServerFrame> resumed = clientA.ProveStream(Options());
        await resumed.RequestStream.WriteAsync(new ClientFrame { SessionId = ready.SessionId, RequestId = 4, Resume = new Resume() });
        Assert.That(await resumed.ResponseStream.MoveNext(), Is.True, "original authenticated owner can resume");
        ServerFrame attachment = resumed.ResponseStream.Current;
        Assert.That((attachment.Ready?.ValidatedThrough, attachment.Epoch), Is.EqualTo(((ulong?)from, ready.Epoch + 1)), "resume preserves progress and fences the old epoch");
        await resumed.RequestStream.CompleteAsync();
        Assert.That(await resumed.ResponseStream.MoveNext(), Is.False, "resumed attachment detaches cleanly");
    }

    [Test]
    public async Task ProveStream_SharedV1Admission_WaitsThenExecutesBlock()
    {
        using BackendAdmission.BackendLease slot = _server.Admission.TryAcquire()!;
        using GrpcChannel channel = _server.Channel(_server.ClientA);
        using AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call = new ProverV2.ProverClient(channel).ProveStream(Options());
        ServerFrame ready = await Begin(call);
        await call.RequestStream.WriteAsync(new ClientFrame
        {
            SessionId = ready.SessionId,
            Epoch = ready.Epoch,
            RequestId = 2,
            Block = new StreamBlock { Data = _server.Chunks[1].Block }
        });
        slot.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await call.ResponseStream.MoveNext(), Is.True, "release of shared admission permits the waiting request");
            Assert.That((call.ResponseStream.Current.RequestId, call.ResponseStream.Current.Validated?.Number),
                Is.EqualTo((2UL, (ulong?)from)), "production v2 validates through the backend shared with v1");
        }
        await call.RequestStream.CompleteAsync();
        Assert.That(await call.ResponseStream.MoveNext(), Is.False, "completed admission does not leave an active backend worker");
    }

    private async Task<ServerFrame> Begin(AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call)
    {
        await call.RequestStream.WriteAsync(new ClientFrame { RequestId = 1, Begin = _server.Anchor });
        Assert.That(await call.ResponseStream.MoveNext(), Is.True, "authenticated Begin returns Ready");
        ServerFrame ready = call.ResponseStream.Current;
        Assert.That(ready.Ready?.ValidatedThrough, Is.EqualTo((ulong)from - 1), "Ready binds the configured rollup anchor");
        return ready;
    }

    private static bool IsTransportRejection(RpcException failure)
    {
        if (failure.StatusCode is StatusCode.Unavailable or StatusCode.Unauthenticated) return true;
        if (failure.StatusCode != StatusCode.Internal) return false;
        for (Exception? cause = failure.Status.DebugException; cause is not null; cause = cause.InnerException)
            if (cause is HttpProtocolException) return true;
        return false;
    }

    private static CallOptions Options(Metadata? headers = null) => new(headers, DateTime.UtcNow.AddSeconds(30));

    internal sealed class EndpointServer : IAsyncDisposable
    {
        private readonly TempPath _directory = TempPath.GetTempDirectory();
        private readonly X509Certificate2 _certificate = Certificate("server", true);
        private readonly WindowLimits _limits;
        private readonly HttpClient _sourceL1;
        private readonly HttpClient _sourceL2;
        private WebApplication _rpc = null!;
        private WebApplication _app = null!;
        private StreamingRuntime? _runtime;
        internal X509Certificate2 ClientA { get; } = Certificate("composer-a", false);
        internal X509Certificate2 ClientB { get; } = Certificate("composer-b", false);
        internal List<ProveChunk> Chunks { get; }
        internal Begin Anchor { get; }
        internal StreamingFinalizerTests.EvidenceHandler L1 { get; }
        internal BackendAdmission Admission { get; private set; } = null!;
        internal Uri PlainAddress { get; private set; } = null!;
        private Uri TlsAddress { get; set; } = null!;

        private EndpointServer(string fixture, int from, int to, WindowLimits? limits, ulong? registeredParentNumber)
        {
            _limits = limits ?? new WindowLimits(8, 16 * 1024 * 1024, 100_000);
            Chunks = AttesterTests.Window(fixture, from, to);
            SettlementBatch batch = EezCalldata.DecodePostAndVerifyBatch(Chunks[0].Header.PostBatch.AbiCalldata.Span);
            Chunks[0].Header.PostBatch.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch with { Proofs = [[]] }));
            BlockHeader parent = Rlp.Decode<BlockHeader>(Chunks[1].Block.Witness.Headers.Single().ToByteArray())!;
            Anchor = new Begin { RollupId = 1, AnchorNumber = parent.Number, AnchorHash = ByteString.CopyFrom(parent.Hash!.Bytes), AnchorStateRoot = ByteString.CopyFrom(parent.StateRoot!.Bytes) };
            JsonElement oracle = StatelessFixtures.ReadJson(fixture, "oracle.json");
            EezSettlementContext context = new(1, 6290, new Address(oracle.GetProperty("proof_system").GetString()!), new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!), 2);
            Dictionary<Hash256, BlockHeader> headers = new() { [parent.Hash!] = parent };
            if (registeredParentNumber is not null)
            {
                foreach (ProveChunk chunk in Chunks.Skip(1))
                {
                    foreach (ByteString encoded in chunk.Block.Witness.Headers)
                    {
                        BlockHeader header = Rlp.Decode<BlockHeader>(encoded.ToByteArray())!;
                        headers.TryAdd(header.Hash!, header);
                    }
                }
            }
            BlockHeader registered = registeredParentNumber is { } number
                ? headers.Values.Single(header => header.Number == number) : parent;
            L1 = new StreamingFinalizerTests.EvidenceHandler(context, registered, false);
            _sourceL1 = new HttpClient(L1);
            _sourceL2 = new HttpClient(new StreamingFinalizerTests.EvidenceHandler(context, parent, true)
            { Headers = registeredParentNumber is null ? null : headers });
        }

        internal static async Task<EndpointServer> Start(string fixture, int from, int to, WindowLimits? limits = null, ulong? registeredParentNumber = null, StreamingSettlementProfile profile = StreamingSettlementProfile.Rollup0Calldata)
        {
            EndpointServer server = new(fixture, from, to, limits, registeredParentNumber);
            try { await server.Start(fixture, profile); return server; }
            catch { await server.DisposeAsync(); throw; }
        }

        private async Task Start(string fixture, StreamingSettlementProfile profile)
        {
            bool conditional = profile == StreamingSettlementProfile.Rollup0Conditional;
            WebApplicationBuilder rpc = WebApplication.CreateSlimBuilder();
            rpc.Logging.ClearProviders();
            rpc.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            _rpc = rpc.Build();
            _rpc.MapPost("/l1", context => Forward(context, _sourceL1));
            _rpc.MapPost("/l2", context => Forward(context, _sourceL2));
            await _rpc.StartAsync();
            Uri source = AddressOf(_rpc, "http");
            Directory.CreateDirectory(_directory.Path);
            File.WriteAllBytes(Path.Combine(_directory.Path, "server.pfx"), _certificate.Export(X509ContentType.Pkcs12, "test"));
            File.WriteAllText(Path.Combine(_directory.Path, "password"), "test");
            File.WriteAllBytes(Path.Combine(_directory.Path, "composer-a.cer"), ClientA.RawData);
            File.WriteAllBytes(Path.Combine(_directory.Path, "composer-b.cer"), ClientB.RawData);
            string config = Path.Combine(_directory.Path, "streaming.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new
            {
                profile = conditional ? "rollup0-conditional" : "rollup0-calldata",
                listenAddress = "127.0.0.1:0",
                l1Rpc = conditional ? null : new Uri(source, "l1").ToString(),
                l2Rpc = conditional ? null : new Uri(source, "l2").ToString(),
                l1ChainId = conditional ? 0 : 31337,
                registry = conditional ? null : TestItem.AddressA.ToString(),
                manager = conditional ? null : TestItem.AddressB.ToString(),
                serverCertificate = "server.pfx",
                serverPasswordFile = "password",
                composerCertificates = new[] { "composer-a.cer", "composer-b.cer" },
                maxSessionBytes = _limits.MaxBytes,
            }));
            ISpecProvider specs = StatelessFixtures.ReadSpecProvider(fixture, "chain-config.json");
            JsonElement oracle = StatelessFixtures.ReadJson(fixture, "oracle.json");
            EezSettlementContext context = new(1, specs.ChainId, new Address(oracle.GetProperty("proof_system").GetString()!),
                new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!), 2);
            AttesterOptions options = new(new IPEndPoint(IPAddress.Loopback, 0), "", 1, context.VerificationKey, AttesterServer.AttesterKey.Address, context.ProofSystem,
                "", "", 2, context.GasLimit, _limits, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), config);
            EezAttestationSigner signer = new(AttesterServer.AttesterKey);
            AttestationPipeline pipeline = new(specs, context, signer, LimboLogs.Instance);
            _runtime = StreamingRuntime.Load(options, specs, pipeline, signer, LimboLogs.Instance);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            _app = Program.Build(builder, options, pipeline, _runtime);
            Admission = _app.Services.GetRequiredService<BackendAdmission>();
            await _app.StartAsync();
            PlainAddress = AddressOf(_app, "http");
            TlsAddress = AddressOf(_app, "https");
        }

        internal GrpcChannel Channel(X509Certificate2? certificate, string? host = null)
        {
            HttpClientHandler handler = new();
            if (certificate is not null) handler.ClientCertificates.Add(certificate);
            string serverPin = _certificate.GetCertHashString(HashAlgorithmName.SHA256);
            handler.ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer?.GetCertHashString(HashAlgorithmName.SHA256) == serverPin;
            HttpClient client = new(handler);
            if (host is not null) client.DefaultRequestHeaders.Host = host;
            return GrpcChannel.ForAddress(TlsAddress, new GrpcChannelOptions { HttpClient = client, DisposeHttpClient = true });
        }

        internal static X509Certificate2 Certificate(string name, bool server)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
            SubjectAlternativeNameBuilder names = new();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], true));
            return request.CreateSelfSigned(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }

        internal static Uri AddressOf(WebApplication app, string scheme) => new(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single(address => address.StartsWith(scheme + ":", StringComparison.Ordinal)));

        private static async Task Forward(HttpContext context, HttpClient client)
        {
            using StreamReader reader = new(context.Request.Body);
            using HttpRequestMessage request = new(HttpMethod.Post, "http://evidence.invalid") { Content = new StringContent(await reader.ReadToEndAsync(context.RequestAborted)) };
            using HttpResponseMessage response = await client.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_app is not null) await Program.StopAndDrain(_app, Admission); }
            finally
            {
                _runtime?.Dispose();
                if (_rpc is not null) { await _rpc.StopAsync(); await _rpc.DisposeAsync(); }
                _sourceL1.Dispose(); _sourceL2.Dispose();
                ClientA.Dispose(); ClientB.Dispose(); _certificate.Dispose();
                _directory.Dispose();
            }
        }
    }
}

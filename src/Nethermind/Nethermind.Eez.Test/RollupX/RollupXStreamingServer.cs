// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Nethermind.Consensus.Stateless;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Block = Nethermind.Core.Block;

namespace Nethermind.Eez.Test.RollupX;

internal sealed class RollupXStreamingServer : IAsyncDisposable
{
    private readonly PrivateKey _key = new("8b3a350cf5c34c9194ca85829a2df0ec3153be0318b5e2d3348e872092edffba");
    private readonly TempPath _directory = TempPath.GetTempDirectory();
    private readonly X509Certificate2 _certificate = StreamingEndpointTests.EndpointServer.Certificate("server", true);
    private readonly X509Certificate2 _client = StreamingEndpointTests.EndpointServer.Certificate("composer", false);
    private StreamingRuntime? _runtime;
    private WebApplication? _rpc;
    private WebApplication? _app;
    private bool _nativeDecoder;
    internal bool IsOffline => _nativeDecoder;
    internal ByteString? ExpectedValidationIdentity { get; private set; }
    private Uri _address = null!;
    private Uri _plainAddress = null!;
    internal BackendAdmission Admission { get; private set; } = null!;

    internal static async Task<RollupXStreamingServer> StartProduction(string directory, JsonElement scenario, bool reorgBeforeSigning = false, Hash256? registryRoot = null)
    {
        RollupXStreamingServer server = new();
        try { await server.StartCore(directory, scenario, reorgBeforeSigning, false, registryRoot); return server; }
        catch { await server.DisposeAsync(); throw; }
    }

    internal static async Task<RollupXStreamingServer> StartFixture(string directory, JsonElement scenario)
    {
        RollupXStreamingServer server = new();
        try { await server.StartCore(directory, scenario, false, true, null); return server; }
        catch { await server.DisposeAsync(); throw; }
    }

    private async Task StartCore(string directory, JsonElement scenario, bool reorgBeforeSigning, bool syntheticAuthority, Hash256? registryRoot)
    {
        string fixture = scenario.GetProperty("fixture").GetString()!;
        using JsonDocument genesis = RollupXRecordedExecutionTests.ReadSelected(directory, fixture + ".genesis.json");
        using JsonDocument l1Genesis = RollupXRecordedExecutionTests.ReadSelected(directory, fixture + ".l1-genesis.json");
        using JsonDocument identity = RollupXRecordedExecutionTests.ReadSelected(directory, fixture + ".identity.json");
        string baseline = fixture == "native-osaka-lookup" ? "baseline-lookup.json" : "baseline-static.json";
        using JsonDocument transcript = RollupXRecordedExecutionTests.ReadSelected(directory, baseline);
        PrivateKey key = _key;
        Assert.That(key.Address, Is.EqualTo(new Address(scenario.GetProperty("oracle").GetProperty("attester").GetString()!)),
            "precondition: the public fixture key matches the independently published signer");
        Directory.CreateDirectory(_directory.Path);
        string chainPath = Path.Combine(_directory.Path, "l2.json");
        File.WriteAllText(chainPath, genesis.RootElement.GetRawText());
        File.WriteAllText(Path.Combine(_directory.Path, "l1.json"), l1Genesis.RootElement.GetRawText());
        (ISpecProvider specs, EezChainRules rules) = ChainConfigLoader.LoadProfile(chainPath);
        EezSettlementContext context = new(identity.RootElement.GetProperty("rollup_id").GetUInt64(), specs.ChainId,
            new Address(identity.RootElement.GetProperty("proof_system").GetString()!),
            new ValueHash256(identity.RootElement.GetProperty("vkey").GetString()!), rules.Schedule!.BlockIntervalSeconds);
        bool offline = rules.NativeFormat is NativeSystemTxFormat.ContractV4 or NativeSystemTxFormat.ContractV6;
        if (offline)
        {
            if (reorgBeforeSigning || registryRoot is not null) throw new ArgumentException("canonical RPC controls require the historical profile");
            _nativeDecoder = true;
            await Program.InitializeExecution(rules, TxDecoder.Instance);
            NativeGenesisSnapshot nativeGenesis = NativeGenesisSnapshot.Load(File.ReadAllBytes(chainPath), rules.NativeFormat);
            NativeL1Rules nativeL1 = NativeL1Rules.LoadEthereum(File.ReadAllBytes(Path.Combine(_directory.Path, "l1.json")));
            NativeOfflineProfile.Inputs network = nativeL1.Profile(nativeGenesis);
            byte[] preimage = Rlp.Encode(
                Rlp.Encode("eez.validation-identity"u8), Rlp.Encode(network.ValidationVersion), Rlp.Encode(context.RollupId), Rlp.Encode(specs.ChainId),
                Rlp.Encode(new Address(identity.RootElement.GetProperty("l2_system_address").GetString()!).Bytes),
                Rlp.Encode(EezConstants.RollupXEezl2Address.Bytes), Rlp.Encode(nativeGenesis.SourceDigest.Bytes),
                Rlp.Encode(NativeOfflineProfile.Compute(in network, nativeL1.RuleTimes).Bytes),
                Rlp.Encode(new Address(identity.RootElement.GetProperty("registry_address").GetString()!).Bytes),
                Rlp.Encode(new Address(identity.RootElement.GetProperty("rollup_manager_address").GetString()!).Bytes)).Bytes;
            ExpectedValidationIdentity = ByteString.CopyFrom(Keccak.Compute(preimage).Bytes);
        }
        if (!offline) await KzgPolynomialCommitments.InitializeAsync();
        FixtureAuthority? authority = offline ? null : new(transcript.RootElement, context, key.Address, rules.Schedule, reorgBeforeSigning, registryRoot);
        Uri? source = null;
        if (authority is not null)
        {
            WebApplicationBuilder rpc = WebApplication.CreateSlimBuilder();
            rpc.Logging.ClearProviders();
            rpc.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            _rpc = rpc.Build();
            _rpc.MapPost("/l1", http => Reply(http, authority, false));
            _rpc.MapPost("/l2", http => Reply(http, authority, true));
            await _rpc.StartAsync();
            source = StreamingEndpointTests.EndpointServer.AddressOf(_rpc, "http");
        }
        File.WriteAllBytes(Path.Combine(_directory.Path, "server.pfx"), _certificate.Export(X509ContentType.Pkcs12, "test"));
        File.WriteAllBytes(Path.Combine(_directory.Path, "composer.cer"), _client.RawData);
        File.WriteAllText(Path.Combine(_directory.Path, "password"), "test");
        JsonElement profile = scenario.GetProperty("server_profile");
        WindowLimits limits = new(profile.GetProperty("max_window_blocks").GetInt32(), profile.GetProperty("max_window_bytes").GetInt64(),
            profile.GetProperty("max_window_witness_items").GetInt64());
        string config = Path.Combine(_directory.Path, "streaming.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            profile = "rollupx-native",
            listenAddress = "127.0.0.1:0",
            l1Rpc = source is null ? null : new Uri(source, "l1").ToString(),
            l2Rpc = source is null ? null : new Uri(source, "l2").ToString(),
            l1ChainId = rules.Schedule.L1Reference.ChainId,
            registry = offline ? identity.RootElement.GetProperty("registry_address").GetString() : TestItem.AddressA.ToString(),
            manager = offline ? identity.RootElement.GetProperty("rollup_manager_address").GetString() : TestItem.AddressB.ToString(),
            l1ChainConfigPath = "l1.json",
            l2GenesisHash = offline ? NativeGenesisSnapshot.Load(File.ReadAllBytes(chainPath), rules.NativeFormat).GenesisHash.ToString() : authority!.Genesis.ToString(),
            serverCertificate = "server.pfx",
            serverPasswordFile = "password",
            composerCertificates = new[] { "composer.cer" },
            maxSessions = 8,
            maxSessionsPerOwner = 8,
            maxConnections = 8,
            maxSessionBytes = limits.MaxBytes,
            maxReservedBytes = checked(limits.MaxBytes * (offline ? 8 : 4) * 8),
        }));
        AttesterOptions options = new(new IPEndPoint(IPAddress.Loopback, 0), chainPath, context.RollupId, context.VerificationKey,
            key.Address, context.ProofSystem, "", "", rules.Schedule.BlockIntervalSeconds, context.GasLimit, limits,
            TimeSpan.FromSeconds(profile.GetProperty("stream_idle_timeout_secs").GetInt32()),
            TimeSpan.FromSeconds(profile.GetProperty("request_timeout_secs").GetInt32()), config);
        EezAttestationSigner signer = new(key);
        AttestationPipeline pipeline = new(specs, context, signer, LimboLogs.Instance);
        StreamingRuntime runtime = StreamingRuntime.Load(options, specs, pipeline, signer, LimboLogs.Instance, rules);
        _runtime = runtime;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (syntheticAuthority && !offline)
        {
            FixtureFinalizer finalizer = new(ChainConfigLoader.LoadL1(Path.Combine(_directory.Path, "l1.json")), specs, rules, context, signer, authority!);
            builder.Host.ConfigureContainer<IServiceCollection>((_, services) => services.Replace(ServiceDescriptor.Singleton<StreamingTransport>(provider =>
                new(runtime.Registry, provider.GetRequiredService<BackendAdmission>(), new StreamingBlockExecutor(specs, rules, LimboLogs.Instance),
                    runtime.Options.TransportLimits, context.RollupId, provider.GetRequiredService<ILogger<StreamingTransport>>(), finalize: finalizer.Finalize))));
        }
        _app = Program.Build(builder, options, pipeline, runtime);
        Admission = _app.Services.GetRequiredService<BackendAdmission>();
        await _app.StartAsync();
        _address = StreamingEndpointTests.EndpointServer.AddressOf(_app, "https");
        _plainAddress = StreamingEndpointTests.EndpointServer.AddressOf(_app, "http");
    }

    internal GrpcChannel Channel()
    {
        HttpClientHandler handler = new();
        handler.ClientCertificates.Add(_client);
        string pin = _certificate.GetCertHashString(HashAlgorithmName.SHA256);
        handler.ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer?.GetCertHashString(HashAlgorithmName.SHA256) == pin;
        return GrpcChannel.ForAddress(_address, new GrpcChannelOptions { HttpClient = new HttpClient(handler), DisposeHttpClient = true });
    }

    internal GrpcChannel PlainChannel() => GrpcChannel.ForAddress(_plainAddress);

    internal static BlockHeader SelectFixtureParent(IReadOnlyDictionary<Hash256, BlockHeader> headers, ulong rollupId, Begin predecessor)
    {
        if (predecessor.RollupId != rollupId || predecessor.AnchorHash.Length != Hash256.Size ||
            !headers.TryGetValue(new Hash256(predecessor.AnchorHash.Span), out BlockHeader? parent) ||
            predecessor.AnchorNumber != parent.Number || !predecessor.AnchorStateRoot.Span.SequenceEqual(parent.StateRoot!.Bytes))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "fixture predecessor differs from the immutable baseline"));
        return parent;
    }

    private static async Task Reply(HttpContext http, FixtureAuthority authority, bool l2)
    {
        using JsonDocument request = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        object result = authority.Read(request.RootElement.GetProperty("method").GetString()!, request.RootElement.GetProperty("params"), l2);
        await http.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = request.RootElement.GetProperty("id").Clone(), result }, http.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_app is not null) await Program.StopAndDrain(_app, Admission); }
        finally
        {
            _runtime?.Dispose();
            if (_nativeDecoder)
            {
                TxDecoder.Instance.RegisterDecoder(new EezNativeSystemTxDecoder());
                TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder());
            }
            if (_rpc is not null) await _rpc.DisposeAsync();
            _certificate.Dispose();
            _client.Dispose();
            _key.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class FixtureFinalizer(ISpecProvider l1Specs, ISpecProvider l2Specs, EezChainRules rules,
        EezSettlementContext context, EezAttestationSigner signer, FixtureAuthority authority)
    {
        private readonly RollupXSettlementExecution _execution = new(l2Specs, rules, context, LimboLogs.Instance);

        internal async Task<Proof> Finalize(SessionOperation operation, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Finalize selection = operation.Finalize ?? throw new InvalidOperationException("not a finalization operation");
            RollupXStreamingFinalizer.ValidateSupportedSettlement(selection.Settlement);
            RollupXSettlementInput input = RollupXSettlementInput.Decode(selection.Settlement, context, l1Specs, cancellation);
            BlockHeader parent = authority.Parent(operation.Predecessor!);
            using AdmittedWindow window = StreamingFinalizer.ToWindow(operation, selection);
            ValueHash256 digest;
            try
            {
                digest = await Task.Run(() => _execution.Validate(window, input, parent, parent.MixHash!, parent.Hash!, cancellation), CancellationToken.None);
            }
            catch (Exception exception) when (exception is EezSettlementException or EezStatelessException or WitnessValidationException or RlpException)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "fixture native settlement validation rejected"));
            }
            return operation.SignCurrent(signer, digest, cancellation);
        }
    }

    private sealed class FixtureAuthority
    {
        private readonly Dictionary<Hash256, BlockHeader> _headers = [];
        private readonly EezSettlementContext _context;
        private readonly Address _signer;
        private readonly RollupXSchedule _schedule;
        private readonly L1PostingContext _target;
        private readonly bool _reorgBeforeSigning;
        private readonly Hash256 _registryRoot;
        private int _targetReads;
        internal Hash256 Genesis { get; }

        internal FixtureAuthority(JsonElement baseline, EezSettlementContext context, Address signer, RollupXSchedule schedule, bool reorgBeforeSigning, Hash256? registryRoot)
        {
            _reorgBeforeSigning = reorgBeforeSigning;
            _context = context;
            _signer = signer;
            _schedule = schedule;
            List<ClientFrame> requests = baseline.GetProperty("events").EnumerateArray().Where(static item => item.GetProperty("direction").GetString() == "client")
                .Select(static item => ClientFrame.Parser.ParseFrom(Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!))).ToList();
            Genesis = new Hash256(requests.Single(static frame => frame.Begin is not null).Begin.AnchorHash.Span);
            _registryRoot = registryRoot ?? Genesis;
            _target = requests.Single(static frame => frame.Finalize is not null).Finalize.Settlement.PostingContext;
            foreach (ClientFrame frame in requests)
            {
                if (frame.Block is null) continue;
                Block block = Rlp.Decode<Block>(frame.Block.Data.Rlp.ToByteArray())!;
                _headers.TryAdd(block.Hash!, block.Header);
                foreach (ByteString encoded in frame.Block.Data.Witness.Headers)
                {
                    BlockHeader parent = Rlp.Decode<BlockHeader>(encoded.ToByteArray())!;
                    _headers.TryAdd(parent.Hash!, parent);
                }
            }
        }

        internal BlockHeader Parent(Begin predecessor) => SelectFixtureParent(_headers, _context.RollupId, predecessor);

        internal object Read(string method, JsonElement parameters, bool l2) => method switch
        {
            "eth_chainId" => Quantity(l2 ? _context.ChainId : _schedule.L1Reference.ChainId),
            "debug_getRawHeader" when l2 => Rlp.Encode(_headers[new Hash256(parameters[0].GetString()!)]).Bytes.ToHexString(true),
            "eth_getBlockByNumber" when !l2 => Header(Convert.ToUInt64(parameters[0].GetString(), 16)),
            "eth_call" when !l2 => Call(parameters),
            "eth_getLogs" when !l2 => Array.Empty<object>(),
            _ => throw new InvalidOperationException("fixture authority has no evidence for this RPC"),
        };

        private object Header(ulong number)
        {
            RollupXL1Reference reference = _schedule.L1Reference;
            if (number != reference.Number && number != _target.ParentNumber)
                throw new InvalidOperationException("fixture authority has no canonical history for this height");
            string hash = number == reference.Number ? reference.Hash.ToString() : new Hash256(_target.ParentHash.Span).ToString();
            if (number == _target.ParentNumber && Interlocked.Increment(ref _targetReads) >= 3 && _reorgBeforeSigning)
                hash = TestItem.KeccakC.ToString();
            return new
            {
                number = Quantity(number),
                hash,
                timestamp = Quantity(number == reference.Number ? reference.Timestamp : _target.TargetTimestamp - _schedule.L1SlotSeconds),
                mixHash = reference.PrevRandao.ToString(),
            };
        }

        private string Call(JsonElement parameters)
        {
            JsonElement call = parameters[0];
            JsonElement namedBlock = parameters[1];
            if (namedBlock.GetProperty("blockHash").GetString() != new Hash256(_target.ParentHash.Span).ToString() ||
                !namedBlock.GetProperty("requireCanonical").GetBoolean()) throw new InvalidOperationException("fixture calls require the declared target");
            byte[] data = Convert.FromHexString(call.GetProperty("data").GetString()!.AsSpan(2));
            Address to = new(call.GetProperty("to").GetString()!);
            byte[] word = new byte[32];
            if (Selector(data, "rollups(uint64)") && to == TestItem.AddressA)
            {
                byte[] registration = new byte[96];
                TestItem.AddressB.Bytes.CopyTo(registration.AsSpan(12));
                _registryRoot.Bytes.CopyTo(registration.AsSpan(32));
                return registration.ToHexString(true);
            }
            if (Selector(data, "verificationKey(address)") && to == TestItem.AddressB) return _context.VerificationKey.Bytes.ToHexString(true);
            if (Selector(data, "signer()") && to == _context.ProofSystem) _signer.Bytes.CopyTo(word.AsSpan(12));
            else if (Selector(data, "threshold()") && to == TestItem.AddressB) word[^1] = 1;
            else throw new InvalidOperationException("fixture authority has no verifier response for this call");
            return word.ToHexString(true);
        }

        private static bool Selector(byte[] data, string signature) => data.AsSpan(0, 4).SequenceEqual(Keccak.Compute(signature).Bytes[..4]);
        private static string Quantity(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    }
}

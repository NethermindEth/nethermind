// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Http;
using Google.Protobuf;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement.RollupX;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Nethermind.Core.Specs;
using Nethermind.Eez.Prove.V2;
using Nethermind.Eez.Execution;
using Nethermind.Logging;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingRuntime : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly HttpClient? _l1;
    private readonly HttpClient? _l2;
    private readonly StreamingBlockExecutor _executor;
    private readonly Func<SessionOperation, CancellationToken, Task<Proof>> _finalize;
    private readonly ulong _rollupId;
    private readonly NativeProver? _native;
    private int _disposed;
    internal StreamingOptions Options { get; }
    internal StreamingIdentity Identity { get; }
    internal StreamingSessionRegistry Registry { get; }
    internal StreamingCapabilities Capabilities { get; }

    private StreamingRuntime(StreamingOptions options, X509Certificate2 certificate, StreamingIdentity identity,
        HttpClient? l1, HttpClient? l2, StreamingSessionRegistry registry, StreamingBlockExecutor executor, Func<SessionOperation, CancellationToken, Task<Proof>> finalize, ulong rollupId, NativeProver? native, StreamingCapabilities capabilities)
    {
        Options = options;
        _certificate = certificate;
        Identity = identity;
        _l1 = l1;
        _l2 = l2;
        Registry = registry;
        _executor = executor;
        _finalize = finalize;
        _rollupId = rollupId;
        _native = native;
        Capabilities = capabilities;
    }

    internal static StreamingRuntime Load(AttesterOptions options, ISpecProvider specs, AttestationPipeline pipeline,
        EezAttestationSigner signer, ILogManager logs, EezChainRules? chainRules = null)
    {
        StreamingOptions settings = StreamingOptions.Load(options.StreamingConfigPath ?? throw StreamingOptions.Invalid(), options);
        if ((settings.Profile == StreamingSettlementProfile.RollupXNative) != (chainRules?.IsRollupX == true))
            throw StreamingOptions.Invalid();
        X509Certificate2? certificate = null;
        HttpClient? l1 = null, l2 = null;
        StreamingSessionRegistry? registry = null;
        try
        {
            string password = Encoding.UTF8.GetString(StreamingOptions.ReadFile(settings.ServerPasswordFile, 4096)).TrimEnd('\r', '\n');
            certificate = X509CertificateLoader.LoadPkcs12(StreamingOptions.ReadFile(settings.ServerCertificate, 1024 * 1024), password,
                X509KeyStorageFlags.EphemeralKeySet);
            DateTime now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                throw StreamingOptions.Invalid();
            X509Certificate2[] pins = new X509Certificate2[settings.ComposerCertificates.Length];
            StreamingIdentity identity;
            try
            {
                for (int i = 0; i < pins.Length; i++)
                {
                    pins[i] = X509CertificateLoader.LoadCertificate(StreamingOptions.ReadFile(settings.ComposerCertificates[i], 64 * 1024));
                    if (now < pins[i].NotBefore.ToUniversalTime() || now > pins[i].NotAfter.ToUniversalTime()) throw StreamingOptions.Invalid();
                }
                identity = new StreamingIdentity(pins);
            }
            finally { foreach (X509Certificate2? pin in pins) pin?.Dispose(); }
            NativeStreamingProfile? nativeProfile = null;
            if (chainRules?.NativeFormat is NativeSystemTxFormat.ContractV4 or NativeSystemTxFormat.ContractV6 or NativeSystemTxFormat.ContractV7)
            {
                NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(StreamingOptions.ReadFile(options.ChainConfigPath, 8 * 1024 * 1024), chainRules.NativeFormat);
                NativeL1Rules l1Rules = NativeL1Rules.Load(settings.L1ChainConfigPath!);
                nativeProfile = new NativeStreamingProfile(genesis, l1Rules);
                if (genesis.Specs.ChainId != specs.ChainId || l1Rules.Specs.ChainId != settings.L1ChainId ||
                    settings.L2GenesisHash?.ValueHash256 != genesis.GenesisHash) throw StreamingOptions.Invalid();
            }
            registry = new StreamingSessionRegistry(settings.RegistryLimits, settings.SessionLimits, TimeProvider.System, settings.Profile, nativeProfile);
            NativeProver? native = null;
            EezChainRules rules;
            Func<SessionOperation, CancellationToken, Task<Proof>> finalize;
            if (settings.Profile != StreamingSettlementProfile.Rollup0Conditional && nativeProfile is null)
            {
                l1 = Source(settings.L1Rpc ?? throw StreamingOptions.Invalid());
                l2 = Source(settings.L2Rpc ?? throw StreamingOptions.Invalid());
            }
            if (settings.Profile == StreamingSettlementProfile.RollupXNative)
            {
                rules = chainRules is { IsRollupX: true } ? chainRules : throw StreamingOptions.Invalid();
                if (nativeProfile is not null)
                {
                    NativeOfflineFinalizer offline = new(nativeProfile, pipeline.Context, settings.Registry!, settings.Manager!, signer, logs);
                    finalize = offline.Finalize;
                    NativeOfflineProfile.Inputs network = nativeProfile.Offline;
                    RollupXValidationIdentity.Inputs input = new(pipeline.Context.RollupId, specs.ChainId,
                        rules.SystemAddress, rules.Eezl2Address, new Hash256(nativeProfile.Genesis.SourceDigest),
                        new Hash256(NativeOfflineProfile.Compute(in network, nativeProfile.L1.RuleTimes)), settings.Registry!, settings.Manager!, network.ValidationVersion);
                    ByteString validationIdentity = ByteString.CopyFrom(RollupXValidationIdentity.Compute(in input).Bytes);
                    native = new NativeProver(validationIdentity);
                }
                else
                {
                    ISpecProvider l1Specs = ChainConfigLoader.LoadL1(settings.L1ChainConfigPath!);
                    if (l1Specs.ChainId != settings.L1ChainId || rules.Schedule!.L1Reference.ChainId != settings.L1ChainId)
                        throw StreamingOptions.Invalid();
                    RollupXParentAuthorizer authorizer = new(new SettlementRpcReader(l1!, TimeSpan.FromSeconds(5)),
                        new SettlementRpcReader(l2!, TimeSpan.FromSeconds(5)), settings.Registry!, settings.Manager!,
                        pipeline.Context, signer.Address, rules.Schedule, settings.L2GenesisHash!);
                    RollupXStreamingFinalizer nativeFinalizer = new(l1Specs, specs, rules, pipeline.Context, signer, authorizer, logs);
                    finalize = nativeFinalizer.Finalize;
                    native = new NativeProver(nativeFinalizer, NativeProver.ValidationIdentity(options, settings, pipeline.Context), options);
                }
            }
            else
            {
                if (chainRules is { IsRollupX: true }) throw StreamingOptions.Invalid();
                rules = pipeline.Rules;
                if (options.SkipConsensusLayerCalls != !rules.CallsBeaconRootsContract) throw StreamingOptions.Invalid();
                if (settings.Profile == StreamingSettlementProfile.Rollup0Conditional)
                {
                    finalize = new Rollup0ConditionalFinalizer(pipeline, signer).Finalize;
                }
                else
                {
                    Rollup0ParentAuthorizer authorizer = new(new SettlementRpcReader(l1!, TimeSpan.FromSeconds(5)),
                        new SettlementRpcReader(l2!, TimeSpan.FromSeconds(5)), settings.L1ChainId, settings.Registry!, settings.Manager!, pipeline.Context, signer.Address);
                    finalize = new StreamingFinalizer(pipeline, signer, authorizer).Finalize;
                }
            }
            return new StreamingRuntime(settings, certificate, identity, l1, l2, registry,
                nativeProfile is null ? new StreamingBlockExecutor(specs, rules, logs) : new StreamingBlockExecutor(nativeProfile, logs),
                finalize, pipeline.Context.RollupId, native, new StreamingCapabilities(nativeProfile is null ? null : native!.Identity, options));
        }
        catch (Exception exception)
        {
            registry?.Dispose();
            l2?.Dispose();
            l1?.Dispose();
            certificate?.Dispose();
            if (exception is CryptographicException or IOException or ArgumentException) throw StreamingOptions.Invalid();
            throw;
        }
    }

    internal ProverService CreateProver(AttestationPipeline pipeline, AttesterOptions options, BackendAdmission admission, ILogger<ProverService> logger) =>
        _native is null ? new ProverService(pipeline, options, admission, logger) :
            new ProverService(_rollupId, _native.Prove, options, admission, logger, _native);

    internal StreamingTransport CreateTransport(BackendAdmission admission, ILogger<StreamingTransport> logger) =>
        new(Registry, admission, _executor, Options.TransportLimits, _rollupId, logger, finalize: _finalize);

    internal void ConfigureListeners(IWebHostBuilder host, AttesterOptions options) => host.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxConcurrentConnections = 64;
        kestrel.Limits.Http2.MaxStreamsPerConnection = 32;
        kestrel.Listen(options.ListenAddress, listen => listen.Protocols = HttpProtocols.Http2);
        kestrel.Listen(Options.ListenAddress, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(tls =>
            {
                tls.ServerCertificate = _certificate;
                tls.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                tls.ClientCertificateValidation = (certificate, _, _) => Identity.IsTrusted(certificate, DateTimeOffset.UtcNow);
                tls.HandshakeTimeout = TimeSpan.FromSeconds(15);
            });
        });
    });

    private static HttpClient Source(Uri uri) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 64,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Registry.Dispose();
        _l2?.Dispose();
        _l1?.Dispose();
        _certificate.Dispose();
    }
}

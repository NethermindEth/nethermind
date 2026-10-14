// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Logging;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingRuntime : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly HttpClient _l1;
    private readonly HttpClient _l2;
    private readonly StreamingBlockExecutor _executor;
    private readonly StreamingFinalizer _finalizer;
    private readonly ulong _rollupId;
    private int _disposed;
    internal StreamingOptions Options { get; }
    internal StreamingIdentity Identity { get; }
    internal StreamingSessionRegistry Registry { get; }

    private StreamingRuntime(StreamingOptions options, X509Certificate2 certificate, StreamingIdentity identity,
        HttpClient l1, HttpClient l2, StreamingSessionRegistry registry, StreamingBlockExecutor executor, StreamingFinalizer finalizer, ulong rollupId)
    {
        Options = options;
        _certificate = certificate;
        Identity = identity;
        _l1 = l1;
        _l2 = l2;
        Registry = registry;
        _executor = executor;
        _finalizer = finalizer;
        _rollupId = rollupId;
    }

    internal static StreamingRuntime Load(AttesterOptions options, ISpecProvider specs, AttestationPipeline pipeline,
        EezAttestationSigner signer, ILogManager logs)
    {
        StreamingOptions settings = StreamingOptions.Load(options.StreamingConfigPath ?? throw StreamingOptions.Invalid(), options);
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
            l1 = Source(settings.L1Rpc);
            l2 = Source(settings.L2Rpc);
            registry = new StreamingSessionRegistry(settings.RegistryLimits, settings.SessionLimits, TimeProvider.System);
            Rollup0ParentAuthorizer authorizer = new(new SettlementRpcReader(l1, TimeSpan.FromSeconds(5)), new SettlementRpcReader(l2, TimeSpan.FromSeconds(5)),
                settings.L1ChainId, settings.Registry, settings.Manager, pipeline.Context, signer.Address);
            return new StreamingRuntime(settings, certificate, identity, l1, l2, registry,
                new StreamingBlockExecutor(specs, EezChainRules.Rollup0, logs), new StreamingFinalizer(pipeline, signer, authorizer), pipeline.Context.RollupId);
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

    internal StreamingTransport CreateTransport(BackendAdmission admission, ILogger<StreamingTransport> logger) =>
        new(Registry, admission, _executor, Options.TransportLimits, _rollupId, logger, finalize: _finalizer.Finalize);

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
        _l2.Dispose();
        _l1.Dispose();
        _certificate.Dispose();
    }
}

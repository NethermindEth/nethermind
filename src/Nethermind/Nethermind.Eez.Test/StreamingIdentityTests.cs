// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Nethermind.Eez.Attester;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class StreamingIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private X509Certificate2 _trusted = null!;
    private StreamingIdentity _identity = null!;

    [SetUp]
    public void SetUp()
    {
        _trusted = Certificate("trusted");
        _identity = new StreamingIdentity([_trusted]);
    }

    [TearDown]
    public void TearDown() => _trusted.Dispose();

    [Test]
    public void Authenticate_PinnedTlsCertificate_UsesStableCertificateOwner()
    {
        using X509Certificate2 copy = X509CertificateLoader.LoadCertificate(_trusted.RawData);
        Assert.That(_identity.Authenticate(true, copy, Now), Is.EqualTo(_trusted.GetCertHashString(HashAlgorithmName.SHA256)),
            "session ownership depends on the authenticated certificate bytes");
    }

    [TestCase("plaintext", TestName = "Authenticate_Plaintext_IsRejected")]
    [TestCase("missing", TestName = "Authenticate_MissingCertificate_IsRejected")]
    [TestCase("unknown", TestName = "Authenticate_UnpinnedCertificate_IsRejected")]
    [TestCase("expired", TestName = "Authenticate_ExpiredCertificate_IsRejected")]
    [TestCase("future", TestName = "Authenticate_NotYetValidCertificate_IsRejected")]
    public void Authenticate_UnverifiedTransport_RejectsWithoutOwner(string mutation)
    {
        using X509Certificate2 other = Certificate("other");
        X509Certificate2? certificate = mutation switch { "missing" => null, "unknown" => other, _ => _trusted };
        DateTimeOffset now = mutation switch { "expired" => new DateTimeOffset(_trusted.NotAfter.ToUniversalTime()).AddDays(1), "future" => new DateTimeOffset(_trusted.NotBefore.ToUniversalTime()).AddDays(-1), _ => Now };
        RpcException exception = Assert.Throws<RpcException>(() => _identity.Authenticate(mutation != "plaintext", certificate, now))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unauthenticated), "wire metadata cannot grant an authenticated owner");
    }

    [Test]
    public void Authenticate_AuthenticatedCallContext_ReturnsPinnedOwner()
    {
        DefaultHttpContext http = new();
        http.Request.Scheme = "https";
        http.Connection.ClientCertificate = _trusted;
        Assert.That(_identity.Authenticate(new AttesterCallContext(http: http, authenticated: true)),
            Is.EqualTo(_trusted.GetCertHashString(HashAlgorithmName.SHA256)), "the actual transport caller accepts the configured authenticated certificate");
    }

    [TestCase(false, true, TestName = "Authenticate_ContextOverPlaintext_IsRejected")]
    [TestCase(true, false, TestName = "Authenticate_ContextWithoutPeerAuthentication_IsRejected")]
    public void Authenticate_CallContextChecks_RejectUnverifiedPeer(bool https, bool authenticated)
    {
        DefaultHttpContext http = new();
        http.Request.Scheme = https ? "https" : "http";
        http.Connection.ClientCertificate = _trusted;
        AttesterCallContext context = new(http: http, authenticated: authenticated);
        context.RequestHeaders.Add("owner", _trusted.GetCertHashString(HashAlgorithmName.SHA256));
        RpcException exception = Assert.Throws<RpcException>(() => _identity.Authenticate(context))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unauthenticated), "a pinned certificate and spoofed metadata cannot bypass either transport authentication check");
    }

    [Test]
    public void Constructor_EmptyCertificatePins_RejectsConfiguration() =>
        Assert.That(() => new StreamingIdentity([]), Throws.ArgumentException, "authentication cannot run with an empty trust set");

    private static X509Certificate2 Certificate(string name)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}

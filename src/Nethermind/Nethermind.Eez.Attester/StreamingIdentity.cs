// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingIdentity
{
    internal const string ValidationIdentityHeader = "eez-validation-identity-bin";

    private readonly HashSet<string> _certificates = [with(StringComparer.Ordinal)];

    internal StreamingIdentity(IEnumerable<X509Certificate2> trustedCertificates)
    {
        foreach (X509Certificate2 certificate in trustedCertificates)
            _certificates.Add(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        if (_certificates.Count == 0) throw new ArgumentException("no trusted composer certificates", nameof(trustedCertificates));
    }

    internal string Authenticate(ServerCallContext context)
    {
        HttpContext http = context.GetHttpContext();
        return Authenticate(http.Request.IsHttps && context.AuthContext.IsPeerAuthenticated, http.Connection.ClientCertificate, DateTimeOffset.UtcNow);
    }

    internal string Authenticate(bool authenticatedTls, X509Certificate2? certificate, DateTimeOffset now)
    {
        if (!authenticatedTls || !IsTrusted(certificate, now)) throw Error();
        return certificate!.GetCertHashString(HashAlgorithmName.SHA256);
    }

    internal static void RequireMatchingValidationIdentity(Metadata headers, ByteString? expectedIdentity)
    {
        if (expectedIdentity is null) return;

        Metadata.Entry received = headers.Get(ValidationIdentityHeader)
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "offline validation identity metadata is required"));
        if (!received.ValueBytes.AsSpan().SequenceEqual(expectedIdentity.Span))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "validation identity mismatch"));
    }

    internal bool IsTrusted(X509Certificate2? certificate, DateTimeOffset now) =>
        certificate is not null && now.UtcDateTime >= certificate.NotBefore.ToUniversalTime() && now.UtcDateTime <= certificate.NotAfter.ToUniversalTime() &&
        _certificates.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256));

    private static RpcException Error() => new(new Status(StatusCode.Unauthenticated, "authenticated composer certificate required"));
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Eez.Test;

internal sealed class AttesterCallContext : ServerCallContext
{
    private readonly CancellationToken _cancellation;
    private readonly AuthContext _authentication;

    internal AttesterCallContext(CancellationToken cancellation = default, HttpContext? http = null, bool authenticated = false)
    {
        _cancellation = cancellation;
        _authentication = new AuthContext(authenticated ? "certificate" : null, new Dictionary<string, List<AuthProperty>>
        {
            ["certificate"] = [AuthProperty.Create("certificate", Encoding.UTF8.GetBytes("trusted"))],
        });
        if (http is not null) UserState["__HttpContext"] = http;
    }

    protected override string MethodCore => "Prove";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "test";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore { get; } = [];
    protected override CancellationToken CancellationTokenCore => _cancellation;
    protected override Metadata ResponseTrailersCore { get; } = [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => _authentication;
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
}

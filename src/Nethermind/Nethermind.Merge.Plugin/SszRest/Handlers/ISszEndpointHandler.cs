// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles a single SSZ-REST engine API endpoint (one HTTP method + resource pair).
/// Implementations are registered with the DI container and injected into
/// <see cref="SszMiddleware"/> as <c>IEnumerable&lt;ISszEndpointHandler&gt;</c>.
/// Each implementation is independently constructable and independently testable.
/// </summary>
public interface ISszEndpointHandler
{
    string HttpMethod { get; }
    string Resource { get; }
    int? Version { get; }

    /// <summary>
    /// Executes the endpoint logic. Called by <see cref="SszMiddleware"/> after auth
    /// has already been validated. <paramref name="body"/> views directly into the
    /// PipeReader's pooled segments — implementations must complete decoding before
    /// performing async work that yields control back to the middleware.
    /// </summary>
    Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body);

    bool AcceptsPathExtra => false;
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

/// <summary>
/// Implemented by result objects that can write themselves directly to a <see cref="PipeWriter"/>,
/// bypassing <see cref="System.Text.Json.Utf8JsonWriter"/> to avoid extra buffer copies.
/// </summary>
public interface IStreamableResult
{
    ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken);
}

internal interface IBatchAwareStreamableResult : IStreamableResult
{
    ValueTask WriteToAsync(PipeWriter writer, bool isBatch, CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by streamable results that write their own JSON-RPC response envelope.
/// </summary>
/// <remarks>
/// The envelope head is buffered rather than pushed to the transport up front, so a result that fails before its
/// first flush can still be replaced by a JSON-RPC error instead of a torn success body. Implementers must not also
/// implement <see cref="IStreamableResultWithStatus"/> or <see cref="IBatchAwareStreamableResult"/>: those are served
/// by the incremental path, which needs the head on the wire before the result runs.
/// </remarks>
internal interface IEnvelopeOwningStreamableResult : IStreamableResult
{
    ValueTask WriteResponseAsync(PipeWriter writer, JsonRpcResponse response, JsonSerializerOptions options, CancellationToken cancellationToken);
}

public enum StreamableResultStatus
{
    Complete,
    Timeout,
    Truncated,
    Cancelled,
    Failed
}

internal interface IStreamableResultWithStatus : IStreamableResult
{
    ValueTask<StreamableResultStatus> WriteToWithStatusAsync(PipeWriter writer, CancellationToken cancellationToken);
}

internal interface IBatchAwareStreamableResultWithStatus : IStreamableResultWithStatus, IBatchAwareStreamableResult
{
    ValueTask<StreamableResultStatus> WriteToWithStatusAsync(PipeWriter writer, bool isBatch, CancellationToken cancellationToken);
}

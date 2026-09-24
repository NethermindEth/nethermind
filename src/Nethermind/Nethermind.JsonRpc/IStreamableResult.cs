// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
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

    /// <summary>
    /// Whether a <see cref="StreamableResultStatus.Complete"/> status is written to the envelope; when <c>false</c>,
    /// <c>_streamStatus</c> appears only for streams that ended early, leaving successful responses unchanged.
    /// </summary>
    bool ReportsCompleteStatus => true;
}

internal interface IBatchAwareStreamableResultWithStatus : IStreamableResultWithStatus, IBatchAwareStreamableResult
{
    ValueTask<StreamableResultStatus> WriteToWithStatusAsync(PipeWriter writer, bool isBatch, CancellationToken cancellationToken);
}

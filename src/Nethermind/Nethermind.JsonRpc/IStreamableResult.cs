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

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

namespace Nethermind.Sockets;

/// <summary>
/// Interface that provides lower level operations (in comparison to <see cref="ISocketsClient"/>)
/// from a specific socket implementation like for example WebSockets, UnixDomainSockets or network sockets.
/// </summary>
public interface ISocketHandler: IAsyncDisposable
{
    PipeReader PipeReader { get; }
    PipeWriter PipeWriter { get; }
    Task Start(CancellationToken cancellationToken);
    Task WriteEndOfMessage(CountingPipeWriter pipeWriter, CancellationToken cancellationToken);
}

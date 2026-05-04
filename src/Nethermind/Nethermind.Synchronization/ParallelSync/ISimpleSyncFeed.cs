// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync;

/// <summary>
/// Simplified sync feed interface for use with <see cref="SimpleDispatcher{T}"/>.
/// PrepareRequest returns null to signal completion. It blocks internally if waiting for work.
/// </summary>
public interface ISimpleSyncFeed<T> where T : class
{
    Task<T?> PrepareRequest(CancellationToken token);
    SyncResponseHandlingResult HandleResponse(T response, PeerInfo? peer = null);
}

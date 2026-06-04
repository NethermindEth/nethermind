// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv4;

internal interface ITaskCompleter<T> : IMessageHandler
{
    TaskCompletionSource<DiscoveryResponse<T>> TaskCompletionSource { get; }
}

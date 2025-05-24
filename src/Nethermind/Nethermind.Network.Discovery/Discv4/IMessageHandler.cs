// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery.Discv4;

internal interface IMessageHandler
{
    bool Handle(DiscoveryMsg msg);
}


internal interface ITaskCompleter<T> : IMessageHandler
{
    TaskCompletionSource<T> TaskCompletionSource { get; }
}

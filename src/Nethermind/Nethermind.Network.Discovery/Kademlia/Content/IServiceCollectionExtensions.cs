// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Network.Discovery.Kademlia.Content;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection ConfigureKademliaContentComponents<TNode, TContentKey, TContent>(this IServiceCollection collection) where TNode : notnull
    {
        return collection
            .AddSingleton<IKademliaContent<TContentKey, TContent>, KademliaContent<TNode, TContentKey, TContent>>()
            .AddSingleton<IContentMessageReceiver<TNode, TContentKey, TContent>, KademliaContentMessageReceiver<TNode, TContentKey, TContent>>()
            .AddSingleton<IContentMessageReceiver<TNode, TContentKey, TContent>, KademliaContentMessageReceiver<TNode, TContentKey, TContent>>();
    }
}

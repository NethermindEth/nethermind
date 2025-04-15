// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Network.Discovery.Kademlia.Content;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Configure an extension of kademlia services to look up content. In particular it provide
    /// an `<see cref="IKademliaContent{TContentKey,TContent}"/> that has a lookup function.
    /// Assume the component for <see cref="IKademlia{TNode}"/> was already registered. In addition to that, it assume
    /// the following dependencies are also available:
    ///
    /// - <see cref="IContentHashProvider{TContentKey}"/>
    /// - <see cref="IKademliaContentStore{TContentKey,TContent}"/>
    /// - <see cref="IContentMessageSender{TNode,TContentKey,TContent}"/>
    ///
    /// Like with main kademlia, the transport is expected to call <see cref="IContentMessageReceiver{TNode,TContentKey,TContent}"/>
    ///
    /// </summary>
    /// <param name="collection"></param>
    /// <typeparam name="TNode"></typeparam>
    /// <typeparam name="TContentKey"></typeparam>
    /// <typeparam name="TContent"></typeparam>
    /// <returns></returns>
    public static IServiceCollection ConfigureKademliaContentComponents<TNode, TContentKey, TContent>(this IServiceCollection collection) where TNode : notnull
    {
        return collection
            .AddSingleton<IKademliaContent<TContentKey, TContent>, KademliaContent<TNode, TContentKey, TContent>>()
            .AddSingleton<IContentMessageReceiver<TNode, TContentKey, TContent>, KademliaContentMessageReceiver<TNode, TContentKey, TContent>>()
            .AddSingleton<IContentMessageReceiver<TNode, TContentKey, TContent>, KademliaContentMessageReceiver<TNode, TContentKey, TContent>>();
    }
}

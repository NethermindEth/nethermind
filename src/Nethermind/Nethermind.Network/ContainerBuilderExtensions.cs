// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.Network;

public static class ContainerBuilderExtensions
{
    public static ContainerBuilder AddMessageSerializer<TMessage, TSerializer>(this ContainerBuilder builder) where TSerializer : class, IZeroMessageSerializer<TMessage> where TMessage : MessageBase
    {
        return builder
            .AddSingleton<IZeroMessageSerializer<TMessage>, TSerializer>()
            .AddSingleton((ctx) => new SerializerInfo(typeof(TMessage), ctx.Resolve<TSerializer>()));
    }

    public static ContainerBuilder AddProtocolHandler<THandler>(
        this ContainerBuilder builder) where THandler : class, IProtocolHandler, IStaticProtocolInfo
    {
        builder.Add<THandler>();

        return builder.AddLast<IProtocolHandlerFactory>(ctx =>
            new ReusableProtocolHandlerFactory<THandler>(ctx.Resolve<Func<ISession, THandler>>(), THandler.Code, THandler.Version));
    }
}

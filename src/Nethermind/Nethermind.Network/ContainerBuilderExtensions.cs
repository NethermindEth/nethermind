// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.Network;

public static class ContainerBuilderExtensions
{
    public static ContainerBuilder AddMessageSerializer<TMessage, TSerializer>(this ContainerBuilder builder) where TSerializer : class, IZeroMessageSerializer<TMessage> where TMessage : MessageBase => builder
            .AddSingleton<IZeroMessageSerializer<TMessage>, TSerializer>()
            .AddSingleton((ctx) => new SerializerInfo(typeof(TMessage), ctx.Resolve<TSerializer>()));

    /// <summary>
    /// Registers a protocol handler type and its corresponding <see cref="IProtocolHandlerFactory"/>.
    /// Handler lifetime is owned by <see cref="ISession"/>: the session disposes its handlers
    /// on disconnect, so the DI container must not track them. The factory uses a cached
    /// constructor activator to avoid Autofac reflection binding on every session.
    /// </summary>
    public static ContainerBuilder AddProtocolHandler<THandler>(
        this ContainerBuilder builder) where THandler : class, IProtocolHandler, IStaticProtocolInfo => builder
            .AddLast<IProtocolHandlerFactory>(ctx =>
                new AutofacProtocolHandlerFactory<THandler>(ctx.Resolve<ILifetimeScope>(), THandler.Code, THandler.Version));

    /// <summary>
    /// Registers a protocol handler that accepts any version (version validation happens
    /// after the Hello handshake, not at factory level). Same ownership semantics
    /// as <see cref="AddProtocolHandler{THandler}(ContainerBuilder)"/>.
    /// </summary>
    public static ContainerBuilder AddProtocolHandler<THandler>(
        this ContainerBuilder builder, string protocolCode) where THandler : class, IProtocolHandler => builder
            .AddLast<IProtocolHandlerFactory>(ctx =>
                new AutofacProtocolHandlerFactory<THandler>(ctx.Resolve<ILifetimeScope>(), protocolCode));
}

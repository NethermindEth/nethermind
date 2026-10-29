// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.Network;

public static class ContainerBuilderExtensions
{
    /// <summary>
    /// Registers the serializer of <typeparamref name="TMessage"/> with <see cref="MessageSerializationService"/>, and
    /// as the serializer interface it implements.
    /// </summary>
    /// <typeparam name="TSerializer">
    /// An <see cref="IMessageSerializer{T}"/> or <see cref="IZeroMessageSerializer{T}"/> of <typeparamref name="TMessage"/>.
    /// The container rejects any other type when it is built.
    /// </typeparam>
    public static ContainerBuilder AddMessageSerializer<TMessage, TSerializer>(this ContainerBuilder builder) where TSerializer : class where TMessage : MessageBase
    {
        Type serializerInterface = typeof(IMessageSerializer<TMessage>).IsAssignableFrom(typeof(TSerializer))
            ? typeof(IMessageSerializer<TMessage>)
            : typeof(IZeroMessageSerializer<TMessage>);

        builder.RegisterType<TSerializer>()
            .As(serializerInterface)
            .AsSelf()
            .WithAttributeFiltering()
            .SingleInstance();

        return builder.AddSingleton((ctx) => new SerializerInfo(typeof(TMessage), ctx.Resolve<TSerializer>()));
    }

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

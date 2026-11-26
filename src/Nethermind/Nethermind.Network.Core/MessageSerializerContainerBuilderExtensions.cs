// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;

namespace Nethermind.Network;

public static class MessageSerializerContainerBuilderExtensions
{
    /// <summary>
    /// Registers the serializer of <typeparamref name="TMessage"/> with <see cref="MessageSerializationService"/>, and
    /// as <see cref="IMessageSerializer{T}"/>.
    /// </summary>
    public static ContainerBuilder AddMessageSerializer<TMessage, TSerializer>(this ContainerBuilder builder) where TSerializer : class, IMessageSerializer<TMessage> where TMessage : MessageBase
    {
        builder.RegisterType<TSerializer>()
            .As<IMessageSerializer<TMessage>>()
            .AsSelf()
            .WithAttributeFiltering()
            .SingleInstance();

        return builder.AddSingleton((ctx) => new SerializerInfo(typeof(TMessage), ctx.Resolve<TSerializer>()));
    }
}

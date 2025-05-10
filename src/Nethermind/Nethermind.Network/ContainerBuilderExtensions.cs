// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;

namespace Nethermind.Network;

public static class ContainerBuilderExtensions
{
    public static ContainerBuilder AddMessageSerializer<TMessage, TSerializer>(this ContainerBuilder builder) where TSerializer : class, IZeroMessageSerializer<TMessage> where TMessage : MessageBase
    {
        return builder
            .AddSingleton<IZeroMessageSerializer<TMessage>, TSerializer>()
            .AddSingleton((ctx) => new SerializerInfo(typeof(TMessage), ctx.Resolve<TSerializer>()));
    }
}

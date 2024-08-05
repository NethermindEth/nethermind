// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using Nethermind.Config;

namespace Nethermind.Network;

public static class BindingExtensions
{
    private static Exception PortInUseException<TConfig>(string optionName, int port)
    {
        Type type = typeof(TConfig);
        string configName = typeof(TConfig).ToString().RemoveEnd("Config");
        if (type.IsInterface) configName = configName.RemoveStart('I');

        return new(
            $"Port {port} is in use. You can change the port used by changing {configName}.{optionName} option."
        );
    }

    private static async Task<IChannel> BindFromConfigAsync<TBootstrap, TChannel, TConfig>(
        this AbstractBootstrap<TBootstrap, TChannel> bootstrap, string optionName, IPAddress address, int port
    ) where TConfig: IConfig where TBootstrap: AbstractBootstrap<TBootstrap, TChannel> where TChannel: IChannel
    {
        try
        {
            return await bootstrap.BindAsync(address, port);
        }
        catch (SocketException exception) when (exception.ErrorCode == 10048)
        {
            throw PortInUseException<TConfig>(optionName, port);
        }
    }

    private static Task<IChannel> BindFromConfigAsync<TBootstrap, TChannel, TConfig>(
        this AbstractBootstrap<TBootstrap, TChannel> bootstrap, string optionName, int port
    ) where TConfig: IConfig where TBootstrap: AbstractBootstrap<TBootstrap, TChannel> where TChannel: IChannel
    {
        return BindFromConfigAsync<TBootstrap, TChannel, TConfig>(bootstrap, optionName, IPAddress.Any, port);
    }

    public static Task<IChannel> BindFromConfigAsync<TConfig>(
        this ServerBootstrap bootstrap, string optionName, IPAddress address, int port
    ) where TConfig: IConfig
    {
        return BindFromConfigAsync<ServerBootstrap, IServerChannel, TConfig>(bootstrap, optionName, address, port);
    }

    public static Task<IChannel> BindFromConfigAsync<TConfig>(
        this ServerBootstrap bootstrap, int port, string optionName
    ) where TConfig: IConfig
    {
        return BindFromConfigAsync<ServerBootstrap, IServerChannel, TConfig>(bootstrap, optionName, port);
    }

    public static Task<IChannel> BindFromConfigAsync<TConfig>(
        this Bootstrap bootstrap, string optionName, IPAddress address, int port
    ) where TConfig: IConfig
    {
        return BindFromConfigAsync<Bootstrap, IChannel, TConfig>(bootstrap, optionName, address, port);
    }

    public static Task<IChannel> BindFromConfigAsync<TConfig>(
        this Bootstrap bootstrap, int port, string optionName
    ) where TConfig: IConfig
    {
        return BindFromConfigAsync<Bootstrap, IChannel, TConfig>(bootstrap, optionName, port);
    }
}

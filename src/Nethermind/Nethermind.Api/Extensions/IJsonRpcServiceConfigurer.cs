// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Api.Extensions
{
    /// <summary>
    /// This is specifically used for JsonRpc services
    /// </summary>
    public interface IJsonRpcServiceConfigurer
    {
        void Configure(IServiceCollection service);
    }
}

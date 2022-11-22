// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Providers.Plugins.JsonRpc;

namespace Nethermind.DataMarketplace.Providers.Plugins
{
    public interface INdmJsonRpcPlugin : INdmPlugin
    {
        string? Host { get; }
        IJsonRpcClient? JsonRpcClient { get; set; }
    }
}

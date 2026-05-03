// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Adapter;

public sealed class NethermindNodeAdapter(INethermindApi api) : INethermindNodeAdapter
{
    private readonly INethermindApi _api = api;

    public NodeVersionDto GetNodeVersion() => new(
        ClientVersion: ProductInfo.ClientId,
        DotNetRuntime: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        EnabledRpcModules: Array.Empty<string>());
}

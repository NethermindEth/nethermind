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

    public SyncStatusDto GetSyncStatus()
    {
        long current = _api.BlockTree?.Head?.Number ?? 0;
        long highest = _api.BlockTree?.BestSuggestedHeader?.Number ?? current;
        long behind = Math.Max(0, highest - current);
        int peerCount = _api.SyncPeerPool?.PeerCount ?? 0;
        string mode = behind == 0 ? "Idle" : "Syncing";

        return new SyncStatusDto(current, highest, mode, behind, peerCount);
    }
}

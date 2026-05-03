// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
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

    public NodeHealthDto GetNodeHealth()
    {
        SyncStatusDto sync = GetSyncStatus();

        string syncStatus = sync.BlocksBehind <= 1 ? "Healthy" : "Degraded";
        string peersStatus = sync.PeerCount switch
        {
            0 => "Unhealthy",
            >= 1 and <= 4 => "Degraded",
            _ => "Healthy",
        };
        string memoryStatus = "Healthy";

        HealthCheckDto[] checks =
        [
            new("sync", syncStatus, $"{sync.BlocksBehind} blocks behind", sync.BlocksBehind),
            new("peers", peersStatus, $"{sync.PeerCount} peers connected", sync.PeerCount),
            new("memory", memoryStatus, null, null),
        ];

        string overall = WorstStatus(syncStatus, peersStatus, memoryStatus);

        using Process process = Process.GetCurrentProcess();
        long uptimeSeconds = Math.Max(0, (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds);
        long memoryMb = process.WorkingSet64 / (1024 * 1024);

        return new NodeHealthDto(
            OverallStatus: overall,
            Checks: checks,
            UptimeSeconds: uptimeSeconds,
            ProcessMemoryMb: memoryMb,
            GcGen0Collections: GC.CollectionCount(0),
            GcGen1Collections: GC.CollectionCount(1),
            GcGen2Collections: GC.CollectionCount(2),
            DiskFreeGb: null,
            DiskUsedGb: null);
    }

    private static string WorstStatus(params string[] statuses)
    {
        if (statuses.Any(static s => s == "Unhealthy")) return "Unhealthy";
        if (statuses.Any(static s => s == "Degraded")) return "Degraded";
        return "Healthy";
    }
}

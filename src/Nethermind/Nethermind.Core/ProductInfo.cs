// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nethermind.Core;

public static class ProductInfo
{
    static ProductInfo()
    {
        var assembly = Assembly.GetEntryAssembly();
        var infoAttr = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var metadataAttrs = assembly?.GetCustomAttributes<AssemblyMetadataAttribute>();
        var productAttr = assembly?.GetCustomAttribute<AssemblyProductAttribute>();
        var commit = metadataAttrs?.FirstOrDefault(a => a.Key.Equals("Commit", StringComparison.Ordinal))?.Value;
        var timestamp = metadataAttrs?.FirstOrDefault(a => a.Key.Equals("BuildTimestamp", StringComparison.Ordinal))?.Value;

        BuildTimestamp = long.TryParse(timestamp, out var t)
            ? DateTimeOffset.FromUnixTimeSeconds(t)
            : DateTimeOffset.MinValue;
        Commit = commit ?? string.Empty;
        Name = productAttr?.Product ?? "Nethermind";
        OS = Platform.GetPlatformName();
        OSArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        Runtime = RuntimeInformation.FrameworkDescription;
        Version = infoAttr?.InformationalVersion ?? string.Empty;

        ClientId = $"{Name}/v{Version}/{OS.ToLowerInvariant()}-{OSArchitecture}/dotnet{Runtime[5..]}";
    }

    public static DateTimeOffset BuildTimestamp { get; }

    public static string ClientId { get; }

    public static string Commit { get; }

    public static string Name { get; }

    public static string OS { get; }

    public static string OSArchitecture { get; }

    public static string Runtime { get; }

    public static string Version { get; }

    public static string Network { get; set; } = "";

    public static string Instance { get; set; } = "";

    public static string SyncType { get; set; } = "";

    public static string PruningMode { get; set; } = "";
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Nethermind.Core;

public static class ProductInfo
{
    static ProductInfo()
    {
        var assembly = Assembly.GetEntryAssembly()!;
        var metadataAttrs = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()!;
        var productAttr = assembly.GetCustomAttribute<AssemblyProductAttribute>()!;
        var versionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!;
        var timestamp = metadataAttrs
            ?.FirstOrDefault(static a => a.Key.Equals("SourceDate", StringComparison.Ordinal))
            ?.Value;

        SourceDate = long.TryParse(timestamp, out var t)
            ? DateTimeOffset.FromUnixTimeSeconds(t)
            : DateTimeOffset.MinValue;
        Name = productAttr?.Product ?? "Nethermind";
        OS = Platform.GetPlatformName();
        OSArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        Runtime = RuntimeInformation.FrameworkDescription;
        Version = versionAttr.InformationalVersion;

        var index = Version.IndexOf('+', StringComparison.Ordinal);

        if (index != -1)
        {
            Commit = Version[(index + 1)..];
            Version = Version[..Math.Min(index + 9, Version.Length - 1)];
        }

        ClientIdParts = new()
        {
            { "name", Name },
            { "version", $"v{Version}" },
            { "os", $"{OS.ToLowerInvariant()}-{OSArchitecture}" },
            { "runtime", $"dotnet{Runtime[5..]}" }
        };

        ClientId = FormatClientId(DefaultPublicClientIdFormat);
        PublicClientId = ClientId;
    }

    public static DateTimeOffset SourceDate { get; }

    private static string FormatClientId(string formatString)
    {
        if (string.IsNullOrEmpty(formatString))
        {
            return string.Empty;
        }

        StringBuilder formattedClientId = new(formatString);
        foreach (var placeholder in ClientIdParts)
        {
            formattedClientId.Replace($"{{{placeholder.Key}}}", placeholder.Value);
        }

        return formattedClientId.ToString();
    }

    public static string ClientId { get; }

    public static string ClientCode { get; } = "NM";

    public static string Commit { get; set; } = string.Empty;

    public static string Name { get; }

    public static string OS { get; }

    public static string OSArchitecture { get; }

    public static string Runtime { get; }

    public static string Version { get; }

    public static string Network { get; set; } = string.Empty;

    public static string Instance { get; set; } = string.Empty;

    public static string SyncType { get; set; } = string.Empty;

    public static string PruningMode { get; set; } = string.Empty;

    private static Dictionary<string, string> ClientIdParts { get; }

    public static string PublicClientId { get; private set; }

    public const string DefaultPublicClientIdFormat = "{name}/{version}/{os}/{runtime}";

    public static void InitializePublicClientId(string formatString) =>
        PublicClientId = FormatClientId(formatString);
}

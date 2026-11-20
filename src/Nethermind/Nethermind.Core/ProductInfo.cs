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
        Assembly assembly = Assembly.GetEntryAssembly()!;
        IEnumerable<AssemblyMetadataAttribute> metadataAttrs = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()!;
        AssemblyProductAttribute productAttr = assembly.GetCustomAttribute<AssemblyProductAttribute>()!;
        AssemblyInformationalVersionAttribute versionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!;
        string? sourceDateEpoch = metadataAttrs
            ?.FirstOrDefault(static a => a.Key.Equals("SourceDateEpoch", StringComparison.Ordinal))
            ?.Value;

        SourceDate = long.TryParse(sourceDateEpoch, out long t)
            ? DateTimeOffset.FromUnixTimeSeconds(t)
            : DateTimeOffset.MinValue;
        Name = productAttr?.Product ?? "Nethermind";
        OS = Platform.GetPlatformName();
        OSArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        Runtime = RuntimeInformation.FrameworkDescription;
        (Version, Commit) = SplitInformationalVersion(versionAttr.InformationalVersion);

        ClientIdParts = new()
        {
            { "name", Name },
            { "version", $"v{Version}" },
            { "versionPostfix", "" },
            { "os", $"{OS.ToLowerInvariant()}-{OSArchitecture}" },
            { "runtime", $"dotnet{Runtime[5..]}" }
        };

        ClientId = FormatClientId(DefaultPublicClientIdFormat);
        PublicClientId = ClientId;
    }

    public static DateTimeOffset SourceDate { get; }

    private const int ShortCommitLength = 8;

    /// <summary>Splits an informational version <c>V+S</c> into the version, with at most the first 8 characters of <c>S</c>, and the whole <c>S</c> as the commit.</summary>
    internal static (string Version, string Commit) SplitInformationalVersion(string informationalVersion)
    {
        int index = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (index == -1) return (informationalVersion, string.Empty);

        string commit = informationalVersion[(index + 1)..];
        int end = commit.Length == 0 ? index : index + 1 + Math.Min(ShortCommitLength, commit.Length);
        return (informationalVersion[..end], commit);
    }

    private static string FormatClientId(string formatString)
    {
        if (string.IsNullOrEmpty(formatString))
        {
            return string.Empty;
        }

        StringBuilder formattedClientId = new(formatString);
        foreach (KeyValuePair<string, string> placeholder in ClientIdParts)
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

    /// <summary>
    /// Suffix appended to the version segment of the public client ID (e.g. <c>-hp</c>, <c>-f</c>) to
    /// advertise the state DB layout.
    /// </summary>
    /// <remarks>
    /// Assigned during network initialization from the resolved DB layout, before the public client ID is rendered.
    /// </remarks>
    public static string VersionPostfix
    {
        get => ClientIdParts["versionPostfix"];
        set => ClientIdParts["versionPostfix"] = value;
    }

    public static string Network { get; set; } = string.Empty;

    public static string Instance { get; set; } = string.Empty;

    public static string SyncType { get; set; } = string.Empty;

    public static string PruningMode { get; set; } = string.Empty;

    private static Dictionary<string, string> ClientIdParts { get; }

    public static string PublicClientId { get; private set; }

    public const string DefaultPublicClientIdFormat = "{name}/{version}{versionPostfix}/{os}/{runtime}";

    public static void InitializePublicClientId(string formatString) =>
        PublicClientId = FormatClientId(formatString);
}

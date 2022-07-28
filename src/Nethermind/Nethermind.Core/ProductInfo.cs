//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
}

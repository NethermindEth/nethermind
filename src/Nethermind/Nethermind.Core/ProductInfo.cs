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
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nethermind.Core;

public static class ProductInfo
{
    static ProductInfo()
    {
        var assembly = Assembly.GetEntryAssembly();
        var gitAttr = assembly?.GetCustomAttribute<GitCommitAttribute>();
        var infoAttr = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var productAttr = assembly?.GetCustomAttribute<AssemblyProductAttribute>();

        CommitHash = gitAttr?.Hash ?? string.Empty;
        Name = productAttr?.Product ?? "Nethermind";
        OS = Platform.GetPlatformName();
        OSArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        Runtime = RuntimeInformation.FrameworkDescription;
        Timestamp = gitAttr?.Timestamp ?? DateTimeOffset.MinValue;
        Version = infoAttr?.InformationalVersion ?? string.Empty;

        UserAgent = $"{Name}/{Version} ({OS}; {OSArchitecture}; {Runtime})";
    }

    public static string CommitHash { get; }

    public static string Name { get; }

    public static string OS { get; }

    public static string OSArchitecture { get; }

    public static string Runtime { get; }

    public static DateTimeOffset Timestamp { get; }

    public static string UserAgent { get; }

    public static string Version { get; }
}

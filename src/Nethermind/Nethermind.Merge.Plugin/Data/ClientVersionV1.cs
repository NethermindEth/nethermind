// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
///   The client version specification.
///   <seealso cref="https://github.com/ethereum/execution-apis/pull/517/files?short_path=f1e647c#diff-f1e647ce063c92e6fd6cd448746b1d1effcfd2fa2e1b031a71f8ce2f74ba0952"/>
/// </summary>
public readonly struct ClientVersionV1
{
    public ClientVersionV1() { }

    /// <summary>Creates a client version from the given fields only.</summary>
    /// <remarks>
    /// The JSON deserialization constructor, so fields a consensus client omits stay empty instead of
    /// defaulting to this client's identity as the parameterless constructor does.
    /// </remarks>
    [JsonConstructor]
    public ClientVersionV1(string code, string name, string version, string commit)
    {
        Code = code;
        Name = name;
        Version = version;
        Commit = commit;
    }

    public string Code { get; init; } = ProductInfo.ClientCode;
    public string Name { get; init; } = ProductInfo.Name;
    public string Version { get; init; } = ProductInfo.Version;
    public string Commit { get; init; } = ProductInfo.Commit.Length < 8
        ? string.Empty.PadLeft(8, '0')
        : ProductInfo.Commit[..8];
}


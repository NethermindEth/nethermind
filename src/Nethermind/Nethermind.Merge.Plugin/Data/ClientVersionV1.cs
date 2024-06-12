// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
///   The client version specification.
///   <seealso cref="https://github.com/ethereum/execution-apis/pull/517/files?short_path=f1e647c#diff-f1e647ce063c92e6fd6cd448746b1d1effcfd2fa2e1b031a71f8ce2f74ba0952"/>
/// </summary>
public readonly struct ClientVersionV1
{
    public ClientVersionV1()
    {
        Code = ProductInfo.ClientCode;
        Name = ProductInfo.Name;
        Version = ProductInfo.Version;
        Commit = ProductInfo.Commit;
    }

    public string Code { get; }
    public string Name { get; }
    public string Version { get; }
    public string Commit { get; }
}


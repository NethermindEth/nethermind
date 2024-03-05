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
        this.code = ProductInfo.ClientCode;
        this.name = ProductInfo.Name;
        this.version = ProductInfo.Version;
        this.commit = ProductInfo.Commit;
    }

    public string code { get; }
    public string name { get; }
    public string version { get; }
    public string commit { get; }

    public override string ToString() => $"{name}/v{version}/{code}";

    public string ToJson() => $"{{\"code\":\"{code}\",\"name\":\"{name}\",\"version\":\"{version}\",\"commit\":\"{commit}\"}}";
}


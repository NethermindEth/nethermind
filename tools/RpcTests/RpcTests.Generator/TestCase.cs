// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Generator;

[SuppressMessage("ReSharper", "UnusedMember.Global", Justification = "Used by dynamic formatter")]
public record TestCase(TestInfo TestInfo, JsonNode Response)
{
    public FilePos Pos => TestInfo.Pos;
    public JsonNode Request => TestInfo.Data;

    public string FileDir => field ??= Path.GetDirectoryName(Pos.FilePath) ?? "";
    public string FileName => field ??= Path.GetFileNameWithoutExtension(Pos.FilePath);
    public string FileExt => field ??= Path.GetExtension(Pos.FilePath);
    public int RequestN => TestInfo.Number;
    public string RequestId => TestInfo.Id;
    public int TestN { get; set; }
}

public record TestInfo(FilePos Pos, int Number, JsonNode Data)
{
    public string Id => Data.GetId() ?? $"{Number}";
}

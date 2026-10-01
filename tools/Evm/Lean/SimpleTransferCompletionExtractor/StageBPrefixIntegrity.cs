// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal static class StageBPrefixIntegrity
{
    internal static string Compute(StageBPrefixProgram program)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(program with { Integrity = "" }));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static string StageClosure(StageBStage stage)
    {
        string[] entries = stage.EntryPoints.Order(StringComparer.Ordinal).Select(static entry => "entry=" + entry).ToArray();
        string[] dependencies = stage.SourceClosure
            .Select(static dependency => $"dependency={dependency.Symbol}|{dependency.Path}|{dependency.SourceSha256}")
            .Order(StringComparer.Ordinal).ToArray();
        string text = string.Join('\n', new[] { "name=" + stage.Name, "theorem=" + stage.Theorem }.Concat(entries).Concat(dependencies));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

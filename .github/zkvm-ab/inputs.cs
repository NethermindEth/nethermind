#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false
#:include ../../scripts/ci/script-process.helpers.cs

// Usage: inputs.cs <checkout> <out> <formats>
// Downloads the pinned blocks of <checkout>, checks their sha256 and, for the Trusted format,
// converts them with the converter of the same checkout. <out>/expected.tsv lists, per format,
// the block, the input file, the expected output and its digest.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

const string BlocksUrl = "https://us-southeast-1.linodeobjects.com/zktests/20261021";

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: inputs.cs <checkout> <out> <formats>");
    return 2;
}

try
{
    var checkout = Path.GetFullPath(args[0]);
    if (!Directory.Exists(checkout))
        throw new InvalidOperationException($"{args[0]} does not exist");
    var output = Path.GetFullPath(args[1]);
    var inputsJson = Path.Combine(checkout, "src/Nethermind/Nethermind.Stateless.ZiskGuest/inputs.json");
    var expected = Path.Combine(output, "expected.tsv");

    Directory.CreateDirectory(output);
    File.WriteAllText(expected, "");
    using var document = JsonDocument.Parse(File.ReadAllText(inputsJson));
    var entries = document.RootElement.EnumerateArray().ToArray();
    var blocks = entries
        .Select(entry => (Input: Field(entry, "input"), Hash: Field(entry, "hash"), Output: Field(entry, "output"), Digest: Field(entry, "digest")))
        .Where(block => InputRegex().IsMatch(block.Input) && HashRegex().IsMatch(block.Hash) && OutputRegex().IsMatch(block.Output) && HashRegex().IsMatch(block.Digest))
        .ToArray();
    if (blocks.Length != entries.Length)
        throw new InvalidOperationException("invalid pinned block set");

    foreach (var block in blocks)
    {
        var path = Path.Combine(output, block.Input);
        var download = await ScriptProcess.Run("curl", ["--retry", "3", "-fsSL", $"{BlocksUrl}/{block.Input}", "-o", path], CancellationToken.None, echo: true, capture: false);
        if (download.Code != 0)
            return download.Code;
        using (var stream = File.OpenRead(path))
        {
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != block.Hash)
                throw new InvalidOperationException($"checksum of {block.Input} failed");
        }
        File.AppendAllText(expected, $"Ssz\t{block.Input}\t{block.Input}\t{block.Output}\t{block.Digest}\n");
    }

    if ($",{args[2]},".Contains(",Trusted,", StringComparison.Ordinal))
    {
        var convert = await ScriptProcess.Run(
            "dotnet",
            ["run", "--project", "tools/StatelessInputGen/StatelessInputGen.csproj", "-c", "release", "--", "convert", "--source", output, "--output", output],
            CancellationToken.None, echo: true, workingDirectory: checkout);
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "convert.log"), convert.Output);
        if (convert.Code != 0)
            return convert.Code;
        var trusted = 0;
        foreach (var line in convert.Output.Split('\n'))
        {
            var match = ConvertedRegex().Match(line);
            if (!match.Success)
                continue;
            var nti = match.Groups[1].Value;
            File.AppendAllText(expected, $"Trusted\t{nti[..^4]}.ssz\t{nti}\t{match.Groups[2].Value}\t{match.Groups[3].Value}\n");
            trusted++;
        }
        if (trusted != entries.Length)
            throw new InvalidOperationException("the conversion did not produce one trusted input per block");
    }

    foreach (var file in Directory.GetFiles(output).Order(StringComparer.Ordinal))
    {
        using var stream = File.OpenRead(file);
        Console.WriteLine($"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {file}");
    }
    Console.Write(File.ReadAllText(expected));
    return 0;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine($"::error::{exception.Message}");
    return 1;
}

static string Field(JsonElement entry, string name) =>
    entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

partial class Program
{
    [GeneratedRegex(@"\A[0-9]+\.ssz\z")]
    private static partial Regex InputRegex();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex HashRegex();
    [GeneratedRegex(@"\A[0-9a-f]{86}\z")]
    private static partial Regex OutputRegex();
    [GeneratedRegex(@"\A([0-9]+\.nti) ([0-9a-f]+) ([0-9a-f]{64})\z")]
    private static partial Regex ConvertedRegex();
}

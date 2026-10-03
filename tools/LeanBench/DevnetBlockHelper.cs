// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Tools.LeanBench;

public static partial class Program
{
    private static void InspectDevnetBlock(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        string input = Value("devnet-block", "");
        if (new FileInfo(input).Length > 24 * 1024 * 1024) throw new InvalidDataException("RPC block exceeds inspection bound");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(input));
        JsonElement block = document.RootElement;
        List<FrameDependency> dependencies = [];
        int rawDeclarations = 0;
        foreach (JsonElement transaction in block.GetProperty("transactions").EnumerateArray())
        {
            if (!transaction.TryGetProperty("frames", out JsonElement frames) || frames.ValueKind == JsonValueKind.Null) continue;
            foreach (JsonElement frame in frames.EnumerateArray())
            {
                string mode = frame.GetProperty("mode").GetString()!;
                if (uint.Parse(mode.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) != (uint)FrameMode.DepVerify) continue;
                string encoded = frame.GetProperty("data").GetString()!;
                byte[] data = Convert.FromHexString(encoded.AsSpan(2));
                if (data.Length % 96 != 0) throw new InvalidDataException("Malformed dependency frame length");
                for (int offset = 0; offset < data.Length; offset += 96)
                {
                    if (data.AsSpan(offset, 31).ContainsAnyExcept((byte)0)
                        || data[offset + 31] is not (Eip8288Constants.LeanSphincsScheme or Eip8288Constants.LeanStarkScheme))
                        throw new InvalidDataException("Malformed dependency declaration");
                }
                rawDeclarations += data.Length / 96;
                dependencies.AddRange(Eip8288Dependencies.Parse(data));
            }
        }
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(dependencies);
        ValueHash256 commitment = Eip8288Dependencies.ComputeDepsHash(canonical);
        string expected = Value("expected-commitment", "");
        if (expected != "" && commitment != new Hash256(expected).ValueHash256)
            throw new InvalidDataException("Body dependency commitment differs from cached block proof");
        object inspection = new
        {
            blockHash = block.GetProperty("hash").GetString(),
            rawDeclarations,
            canonicalDependencies = canonical.Count,
            signatures = canonical.Count(d => d.Scheme == Eip8288Constants.LeanSphincsScheme),
            genericStarks = canonical.Count(d => d.Scheme == Eip8288Constants.LeanStarkScheme),
            dependencyHash = commitment.ToString(),
            cachedProofCommitmentMatchesBody = expected != ""
        };
        File.WriteAllText(Value("out", input + ".inspection.json"), JsonSerializer.Serialize(inspection, new JsonSerializerOptions { WriteIndented = true }));
    }
}

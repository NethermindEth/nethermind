// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Xml;

namespace JitAsm;

internal static class InstructionDbBuilder
{
    // Map from CLI flag value to uops.info architecture name
    private static readonly Dictionary<string, string> ArchMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alder-lake"] = "ADL-P",
        ["rocket-lake"] = "RKL",
        ["ice-lake"] = "ICL",
        ["tiger-lake"] = "TGL",
        ["skylake"] = "SKL",
        ["zen4"] = "ZEN4",
        ["zen3"] = "ZEN3",
        ["zen2"] = "ZEN2",
    };

    // Fallback chain: if the target arch has no data, try these in order
    private static readonly Dictionary<string, string[]> FallbackChain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADL-P"] = ["RKL", "TGL", "ICL", "SKL"],
        ["RKL"] = ["TGL", "ICL", "SKL"],
        ["TGL"] = ["ICL", "SKL"],
        ["ICL"] = ["SKL", "SKX", "HSW"],
        ["SKL"] = ["SKX", "HSW"],
        ["ZEN4"] = ["ZEN3", "ZEN2", "ZEN+"],
        ["ZEN3"] = ["ZEN2", "ZEN+"],
        ["ZEN2"] = ["ZEN+"],
    };

    public static string ResolveArchName(string cliValue)
    {
        return ArchMap.TryGetValue(cliValue, out var name) ? name : cliValue.ToUpperInvariant();
    }

    public static IReadOnlyCollection<string> SupportedArchitectures => ArchMap.Keys;

    public static InstructionDb Build(string xmlPath, string archCliValue)
    {
        string targetArch = ResolveArchName(archCliValue);
        string[] fallbacks = FallbackChain.TryGetValue(targetArch, out var fb) ? fb : [];

        var db = new InstructionDb(targetArch);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stream = File.OpenRead(xmlPath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "instruction")
                continue;

            string? asm = reader.GetAttribute("asm");
            if (asm is null)
                continue;

            // Clean the asm mnemonic: remove prefixes like "{load} " or "{store} "
            string mnemonic = CleanMnemonic(asm);
            if (mnemonic.Length == 0)
                continue;

            // Read the instruction subtree
            string instructionXml = reader.ReadOuterXml();
            var instrDoc = new XmlDocument();
            instrDoc.LoadXml(instructionXml);
            var instrNode = instrDoc.DocumentElement!;

            // Parse operands
            string operandPattern = BuildOperandPattern(instrNode);

            // Dedup key
            string key = $"{mnemonic.ToLowerInvariant()}|{operandPattern}";
            if (seen.Contains(key))
                continue;

            // Find measurement for target architecture (with fallback)
            var measurement = FindMeasurement(instrNode, targetArch, fallbacks);
            if (measurement is null)
                continue;

            float throughput = ParseFloat(measurement.GetAttribute("TP_unrolled"))
                ?? ParseFloat(measurement.GetAttribute("TP_loop"))
                ?? 0;

            int uops = ParseInt(measurement.GetAttribute("uops")) ?? 0;
            string? ports = measurement.GetAttribute("ports");
            if (string.IsNullOrEmpty(ports)) ports = null;

            // Get max latency from child <latency> elements
            int latency = 0;
            foreach (XmlNode child in measurement.ChildNodes)
            {
                if (child is XmlElement latencyEl && latencyEl.Name == "latency")
                {
                    int? cycles = ParseInt(latencyEl.GetAttribute("cycles"))
                        ?? ParseInt(latencyEl.GetAttribute("cycles_mem"))
                        ?? ParseInt(latencyEl.GetAttribute("cycles_addr"));
                    if (cycles.HasValue && cycles.Value > latency)
                        latency = cycles.Value;
                }
            }

            seen.Add(key);
            db.Add(new InstructionInfo
            {
                Mnemonic = mnemonic.ToLowerInvariant(),
                OperandPattern = operandPattern,
                Throughput = throughput,
                Latency = latency,
                Uops = uops,
                Ports = ports
            });
        }

        return db;
    }

    private static string CleanMnemonic(string asm)
    {
        // Remove assembler hints like "{load} ", "{store} ", "{vex} ", "{evex} "
        ReadOnlySpan<char> span = asm.AsSpan().Trim();
        while (span.Length > 0 && span[0] == '{')
        {
            int end = span.IndexOf('}');
            if (end < 0) break;
            span = span[(end + 1)..].TrimStart();
        }

        // Take only the first word (mnemonic), skip any operand hints
        int space = span.IndexOf(' ');
        if (space > 0)
            span = span[..space];

        return span.ToString();
    }

    private static string BuildOperandPattern(XmlElement instrNode)
    {
        var parts = new List<string>();
        foreach (XmlNode child in instrNode.ChildNodes)
        {
            if (child is not XmlElement operandEl || operandEl.Name != "operand")
                continue;

            // Skip suppressed operands (flags, implicit registers)
            if (operandEl.GetAttribute("suppressed") == "1")
                continue;

            string? type = operandEl.GetAttribute("type");
            string? width = operandEl.GetAttribute("width");

            string part = type switch
            {
                "reg" => ClassifyReg(width, operandEl.InnerText),
                "mem" => ClassifyMem(width),
                "agen" => "agen",
                "imm" => ClassifyImm(width),
                "relbr" => "rel",
                _ => ""
            };

            if (part.Length > 0)
                parts.Add(part);
        }

        return string.Join(",", parts);
    }

    private static string ClassifyReg(string? width, string? regNames)
    {
        // Check register names for xmm/ymm/zmm/mm/k
        if (regNames is not null)
        {
            string firstReg = regNames.Split(',')[0].Trim().ToUpperInvariant();
            if (firstReg.StartsWith("ZMM")) return "zmm";
            if (firstReg.StartsWith("YMM")) return "ymm";
            if (firstReg.StartsWith("XMM")) return "xmm";
            if (firstReg.StartsWith("MM")) return "mm";
            if (firstReg.StartsWith("K")) return "k";
        }

        return width switch
        {
            "8" => "r8",
            "16" => "r16",
            "32" => "r32",
            "64" => "r64",
            "128" => "xmm",
            "256" => "ymm",
            "512" => "zmm",
            _ => "r"
        };
    }

    private static string ClassifyMem(string? width)
    {
        return width switch
        {
            "8" => "m8",
            "16" => "m16",
            "32" => "m32",
            "64" => "m64",
            "128" => "m128",
            "256" => "m256",
            "512" => "m512",
            _ => "m"
        };
    }

    private static string ClassifyImm(string? width)
    {
        return width switch
        {
            "8" => "imm8",
            "16" => "imm16",
            "32" => "imm32",
            _ => "imm"
        };
    }

    private static XmlElement? FindMeasurement(XmlElement instrNode, string targetArch, string[] fallbacks)
    {
        // Try target arch first, then fallbacks
        var archsToTry = new List<string> { targetArch };
        archsToTry.AddRange(fallbacks);

        foreach (string archName in archsToTry)
        {
            foreach (XmlNode child in instrNode.ChildNodes)
            {
                if (child is not XmlElement archEl || archEl.Name != "architecture")
                    continue;
                if (archEl.GetAttribute("name") != archName)
                    continue;

                foreach (XmlNode archChild in archEl.ChildNodes)
                {
                    if (archChild is XmlElement measureEl && measureEl.Name == "measurement")
                        return measureEl;
                }
            }
        }

        return null;
    }

    private static float? ParseFloat(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : null;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : null;
    }
}

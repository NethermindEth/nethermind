// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.RegularExpressions;

namespace JitAsm;

internal static partial class InstructionAnnotator
{
    // Matches JIT assembly instruction lines:
    //   "       add      rax, rcx"
    //   "       mov      dword ptr [rbp+0x10], eax"
    [GeneratedRegex(@"^\s+(?<mnemonic>[a-z]\w*)\s+(?<operands>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstructionLineRegex();

    // Matches zero-operand instructions: "       ret" or "       nop"
    [GeneratedRegex(@"^\s+(?<mnemonic>[a-z]\w*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroOperandRegex();

    // 64-bit registers
    private static readonly HashSet<string> Regs64 = new(StringComparer.OrdinalIgnoreCase)
    {
        "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rsp", "rbp",
        "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"
    };

    // 32-bit registers
    private static readonly HashSet<string> Regs32 = new(StringComparer.OrdinalIgnoreCase)
    {
        "eax", "ebx", "ecx", "edx", "esi", "edi", "esp", "ebp",
        "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d"
    };

    // 16-bit registers
    private static readonly HashSet<string> Regs16 = new(StringComparer.OrdinalIgnoreCase)
    {
        "ax", "bx", "cx", "dx", "si", "di", "sp", "bp",
        "r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w", "r15w"
    };

    // 8-bit registers
    private static readonly HashSet<string> Regs8 = new(StringComparer.OrdinalIgnoreCase)
    {
        "al", "bl", "cl", "dl", "sil", "dil", "spl", "bpl", "ah", "bh", "ch", "dh",
        "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b"
    };

    // JIT mnemonic → uops.info mnemonic mapping for conditional jumps
    // JIT uses Intel-style aliases (je, jne, ja, etc.) but uops.info uses the
    // canonical forms (jz, jnz, jnbe, etc.)
    private static readonly Dictionary<string, string> MnemonicAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["je"] = "jz",
        ["jne"] = "jnz",
        ["ja"] = "jnbe",
        ["jae"] = "jnb",
        ["jb"] = "jb",       // canonical
        ["jbe"] = "jna",
        ["jg"] = "jnle",
        ["jge"] = "jnl",
        ["jl"] = "jnge",
        ["jle"] = "jng",
        ["jc"] = "jb",
        ["jnc"] = "jnb",
        ["jp"] = "jp",       // canonical
        ["jnp"] = "jnp",     // canonical
        ["js"] = "js",       // canonical
        ["jns"] = "jns",     // canonical
        ["jo"] = "jo",       // canonical
        ["jno"] = "jno",     // canonical
        ["cmove"] = "cmovz",
        ["cmovne"] = "cmovnz",
        ["cmova"] = "cmovnbe",
        ["cmovae"] = "cmovnb",
        ["cmovb"] = "cmovb", // canonical
        ["cmovbe"] = "cmovna",
        ["cmovg"] = "cmovnle",
        ["cmovge"] = "cmovnl",
        ["cmovl"] = "cmovnge",
        ["cmovle"] = "cmovng",
        ["sete"] = "setz",
        ["setne"] = "setnz",
        ["seta"] = "setnbe",
        ["setae"] = "setnb",
        ["setb"] = "setb",   // canonical
        ["setbe"] = "setna",
        ["setg"] = "setnle",
        ["setge"] = "setnl",
        ["setl"] = "setnge",
        ["setle"] = "setng",
    };

    public static string Annotate(string disassembly, InstructionDb db)
    {
        var sb = new StringBuilder();
        var lines = JoinContinuationLines(disassembly.Split('\n'));

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            // Skip comment lines, labels, directives
            if (IsNonInstructionLine(line))
            {
                sb.AppendLine(line);
                continue;
            }

            var match = InstructionLineRegex().Match(line);
            if (match.Success)
            {
                string mnemonic = match.Groups["mnemonic"].Value.ToLowerInvariant();
                string operandsRaw = match.Groups["operands"].Value.Trim();

                // Skip annotations for calls and jumps to labels
                if (ShouldSkipAnnotation(mnemonic, operandsRaw))
                {
                    sb.AppendLine(line);
                    continue;
                }

                string pattern = ClassifyOperands(operandsRaw, mnemonic);

                // Try the original mnemonic first, then any alias
                string lookupMnemonic = MnemonicAliases.TryGetValue(mnemonic, out var alias)
                    ? alias : mnemonic;
                var info = db.Lookup(mnemonic, pattern)
                    ?? (lookupMnemonic != mnemonic ? db.Lookup(lookupMnemonic, pattern) : null);

                if (info is not null)
                {
                    string annotation = FormatAnnotation(info);
                    // Pad the line to align annotations
                    int padTo = Math.Max(line.Length + 1, 55);
                    sb.Append(line.PadRight(padTo));
                    sb.AppendLine(annotation);
                }
                else
                {
                    sb.AppendLine(line);
                }
                continue;
            }

            // Try zero-operand match
            var zeroMatch = ZeroOperandRegex().Match(line);
            if (zeroMatch.Success)
            {
                string mnemonic = zeroMatch.Groups["mnemonic"].Value.ToLowerInvariant();
                if (!ShouldSkipAnnotation(mnemonic, ""))
                {
                    string zeroLookup = MnemonicAliases.TryGetValue(mnemonic, out var zAlias)
                        ? zAlias : mnemonic;
                    var info = db.Lookup(mnemonic, "")
                        ?? (zeroLookup != mnemonic ? db.Lookup(zeroLookup, "") : null);
                    if (info is not null)
                    {
                        string annotation = FormatAnnotation(info);
                        int padTo = Math.Max(line.Length + 1, 55);
                        sb.Append(line.PadRight(padTo));
                        sb.AppendLine(annotation);
                        continue;
                    }
                }
            }

            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Joins JIT output continuation lines. The JIT wraps long lines at ~80 chars:
    ///   "       call     \n[System.Threading.ThreadLocal`1[...]:get_Value():...]\n"
    ///   "; Assembly listing for method \nNamespace.Type:Method(...)\n"
    /// This joins them so each logical line is a single string.
    /// </summary>
    private static List<string> JoinContinuationLines(string[] rawLines)
    {
        var result = new List<string>(rawLines.Length);
        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].TrimEnd('\r');

            // Keep joining while the next line looks like a continuation
            while (i + 1 < rawLines.Length)
            {
                string next = rawLines[i + 1].TrimEnd('\r');
                if (IsContinuationLine(next))
                {
                    // Preserve a single space between joined parts so "call \n[Type:Method]"
                    // becomes "call [Type:Method]" rather than "call[Type:Method]"
                    line = line.TrimEnd() + " " + next.TrimStart();
                    i++;
                }
                else
                {
                    break;
                }
            }

            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// A line is a continuation if it doesn't match any known "primary" line type:
    /// blank, comment (;), label (G_M...:), instruction (leading whitespace), data (RWD), or alignment.
    /// </summary>
    private static bool IsContinuationLine(string line)
    {
        if (line.Length == 0) return false;

        ReadOnlySpan<char> trimmed = line.AsSpan().TrimEnd('\r');
        if (trimmed.Length == 0) return false;

        // Instructions start with whitespace
        if (char.IsWhiteSpace(trimmed[0])) return false;

        // Comments start with ';'
        if (trimmed[0] == ';') return false;

        // Labels: "G_M000_IG01:" or similar identifiers ending with ':'
        // Check if line contains ':' and starts with a label-like pattern
        if (trimmed.StartsWith("G_M", StringComparison.Ordinal) && trimmed.Contains(":", StringComparison.Ordinal))
            return false;

        // Read-only data table entries: "RWD00  dd  ..."
        if (trimmed.StartsWith("RWD", StringComparison.Ordinal)) return false;

        // Alignment directives: "align [N bytes for IG...]"
        if (trimmed.StartsWith("align", StringComparison.OrdinalIgnoreCase)) return false;

        // Everything else is a continuation of the previous line
        return true;
    }

    private static bool IsNonInstructionLine(string line)
    {
        if (line.Length == 0) return true;

        // Instructions always start with whitespace (indented).
        // Labels, data tables, directives, and other non-instruction lines start at column 0.
        if (line[0] == ';') return true;              // Comment at column 0
        if (!char.IsWhiteSpace(line[0])) return true; // Labels (G_M000_IG01:), data (RWD00), directives, etc.

        // Indented comments: "  ; comment"
        ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == ';') return true;

        return false;
    }

    private static bool ShouldSkipAnnotation(string mnemonic, string operands)
    {
        // Skip calls (to runtime helpers, methods, etc.)
        if (mnemonic == "call") return true;

        // Skip ret - uops.info TP_unrolled is a microbenchmark artifact (return stack buffer
        // mispredictions make the measurement meaningless for real code)
        if (mnemonic == "ret") return true;

        // Skip int3/nop - not meaningful for performance analysis
        if (mnemonic is "int3" or "nop" or "int") return true;

        return false;
    }

    internal static string ClassifyOperands(string operandsRaw, string? mnemonic = null)
    {
        // Handle trailing comments after operands: "rax, rcx ; some comment"
        int commentIdx = operandsRaw.IndexOf(';');
        if (commentIdx >= 0)
            operandsRaw = operandsRaw[..commentIdx].TrimEnd();

        if (string.IsNullOrWhiteSpace(operandsRaw))
            return "";

        // Split operands by comma, but respect brackets for memory operands
        var operands = SplitOperands(operandsRaw);
        var parts = new List<string>();

        for (int i = 0; i < operands.Count; i++)
        {
            string op = operands[i].Trim();

            // LEA's second operand is an address expression, not a memory load
            // uops.info classifies it as "agen" (address generation)
            if (mnemonic is "lea" && i == 1 && op.Contains('['))
            {
                parts.Add("agen");
                continue;
            }

            string classified = ClassifySingleOperand(op);
            if (classified.Length > 0)
                parts.Add(classified);
        }

        return string.Join(",", parts);
    }

    private static List<string> SplitOperands(string operands)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < operands.Length; i++)
        {
            char c = operands[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(operands[start..i]);
                start = i + 1;
            }
        }

        result.Add(operands[start..]);
        return result;
    }

    private static string ClassifySingleOperand(string op)
    {
        // Memory operand: "dword ptr [rbp+10h]", "qword ptr [rsp+20h]", "[rax]"
        if (op.Contains('['))
        {
            if (op.Contains("zmmword ptr", StringComparison.OrdinalIgnoreCase)) return "m512";
            if (op.Contains("ymmword ptr", StringComparison.OrdinalIgnoreCase)) return "m256";
            if (op.Contains("xmmword ptr", StringComparison.OrdinalIgnoreCase)) return "m128";
            if (op.Contains("qword ptr", StringComparison.OrdinalIgnoreCase)) return "m64";
            // gword ptr = GC-tracked pointer-width memory (.NET JIT specific, equivalent to qword on x64)
            if (op.Contains("gword ptr", StringComparison.OrdinalIgnoreCase)) return "m64";
            // bword ptr = pointer-width memory without GC tracking (.NET JIT specific, equivalent to qword on x64)
            if (op.Contains("bword ptr", StringComparison.OrdinalIgnoreCase)) return "m64";
            if (op.Contains("dword ptr", StringComparison.OrdinalIgnoreCase)) return "m32";
            if (op.Contains("word ptr", StringComparison.OrdinalIgnoreCase) &&
                !op.Contains("dword", StringComparison.OrdinalIgnoreCase) &&
                !op.Contains("qword", StringComparison.OrdinalIgnoreCase) &&
                !op.Contains("gword", StringComparison.OrdinalIgnoreCase) &&
                !op.Contains("bword", StringComparison.OrdinalIgnoreCase) &&
                !op.Contains("xmmword", StringComparison.OrdinalIgnoreCase) &&
                !op.Contains("ymmword", StringComparison.OrdinalIgnoreCase) &&
                !op.Contains("zmmword", StringComparison.OrdinalIgnoreCase))
                return "m16";
            if (op.Contains("byte ptr", StringComparison.OrdinalIgnoreCase)) return "m8";
            return "m";
        }

        // Register operands
        string regName = op.Trim();

        // ZMM registers
        if (regName.StartsWith("zmm", StringComparison.OrdinalIgnoreCase)) return "zmm";
        // YMM registers
        if (regName.StartsWith("ymm", StringComparison.OrdinalIgnoreCase)) return "ymm";
        // XMM registers
        if (regName.StartsWith("xmm", StringComparison.OrdinalIgnoreCase)) return "xmm";
        // K mask registers
        if (regName.StartsWith("k", StringComparison.OrdinalIgnoreCase) && regName.Length <= 2 &&
            regName.Length > 1 && char.IsDigit(regName[1])) return "k";

        if (Regs64.Contains(regName)) return "r64";
        if (Regs32.Contains(regName)) return "r32";
        if (Regs16.Contains(regName)) return "r16";
        if (Regs8.Contains(regName)) return "r8";

        // Immediate: hex (0x1A, 1Ah), decimal, or negative
        if (IsImmediate(regName))
        {
            // Try to determine imm8 vs imm32 from value range
            if (TryParseImmediate(regName, out long value))
            {
                return value is >= -128 and <= 255 ? "imm8" : "imm32";
            }
            return "imm";
        }

        // Label reference (for jumps) - strip SHORT/NEAR prefix added by JIT
        if (regName.StartsWith("SHORT ", StringComparison.OrdinalIgnoreCase))
            regName = regName[6..].TrimStart();
        if (regName.StartsWith("NEAR ", StringComparison.OrdinalIgnoreCase))
            regName = regName[5..].TrimStart();

        if (regName.StartsWith("G_M", StringComparison.OrdinalIgnoreCase))
            return "rel";

        // Unknown
        return "";
    }

    private static bool IsImmediate(string op)
    {
        if (op.Length == 0) return false;

        // Hex: 0x prefix or trailing h
        if (op.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return true;
        if (op.EndsWith('h') || op.EndsWith('H'))
        {
            return op[..^1].All(c => char.IsAsciiHexDigit(c) || c == '-');
        }

        // Decimal (possibly negative)
        return op.All(c => char.IsDigit(c) || c == '-');
    }

    private static bool TryParseImmediate(string op, out long value)
    {
        value = 0;

        if (op.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(op.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);

        if (op.EndsWith('h') || op.EndsWith('H'))
            return long.TryParse(op.AsSpan(0, op.Length - 1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);

        return long.TryParse(op, out value);
    }

    private static string FormatAnnotation(InstructionInfo info)
    {
        var sb = new StringBuilder();
        sb.Append("; [");
        sb.Append($"TP:{info.Throughput:F2}");
        sb.Append($" | Lat:{info.Latency,2}");
        sb.Append($" | Uops:{info.Uops}");
        if (info.Ports is not null)
        {
            sb.Append($" | {info.Ports}");
        }
        sb.Append(']');
        return sb.ToString();
    }
}

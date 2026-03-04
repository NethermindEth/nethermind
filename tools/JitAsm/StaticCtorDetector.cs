// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace JitAsm;

internal static partial class StaticCtorDetector
{
    // Patterns for detecting static constructor calls in JIT disassembly
    // Example: call     Namespace.Type:.cctor()
    [GeneratedRegex(@"call\s+(?<type>[\w.+`\[\],]+):\.cctor\(\)", RegexOptions.Compiled)]
    private static partial Regex CctorCallPattern();

    // JIT helpers for static field initialization
    // Matches both shared and non-shared variants, with optional brackets:
    //   call CORINFO_HELP_GETSHARED_NONGCSTATIC_BASE
    //   call [CORINFO_HELP_GET_NONGCSTATIC_BASE]
    //   call CORINFO_HELP_CLASSINIT_SHARED_DYNAMICCLASS
    [GeneratedRegex(@"call\s+\[?CORINFO_HELP_(?:(?:GETSHARED_|GET_)(?:NON)?GCSTATIC_BASE|CLASSINIT_SHARED_DYNAMICCLASS)\]?", RegexOptions.Compiled)]
    private static partial Regex StaticHelperPattern();

    // Pattern for type references in static helper context
    // The JIT output often shows the type being initialized nearby
    // Example: ; Namespace.Type
    [GeneratedRegex(@";\s*(?<type>[\w.+`\[\],]+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex TypeCommentPattern();

    // Pattern for detecting lazy initialization checks
    // Example: cmp     dword ptr [Namespace.Type:initialized], 0
    [GeneratedRegex(@"\[(?<type>[\w.+`\[\],]+):", RegexOptions.Compiled)]
    private static partial Regex StaticFieldAccessPattern();

    // Pattern for extracting types from call targets in JIT output.
    // In diffable mode, long call targets wrap to the next line, e.g.:
    //   call
    //   [Nethermind.Core.Eip4844Constants:get_MinBlobGasPrice():...]
    // In non-diffable mode, they appear on one line:
    //   call [Nethermind.Core.Eip4844Constants:get_MinBlobGasPrice():...]
    // This pattern matches the "[Type:Method" portion (possibly with [r11] prefix for callvirt).
    [GeneratedRegex(@"(?:\[(?:r\d+\])?)?\[?(?<type>[\w.+`\[\],]+):(?!:)", RegexOptions.Compiled)]
    private static partial Regex CallTargetTypePattern();

    public static IReadOnlyList<string> DetectStaticCtors(string disassemblyOutput)
    {
        var detectedTypes = new HashSet<string>(StringComparer.Ordinal);

        // Detect direct .cctor calls
        foreach (Match match in CctorCallPattern().Matches(disassemblyOutput))
        {
            var typeName = NormalizeTypeName(match.Groups["type"].Value);
            if (!string.IsNullOrEmpty(typeName))
            {
                detectedTypes.Add(typeName);
            }
        }

        // Check for static helper calls and try to find associated types
        if (StaticHelperPattern().IsMatch(disassemblyOutput))
        {
            int typesBeforeHelperScan = detectedTypes.Count;

            // Look for type references near the helper calls (±3 lines)
            var lines = disassemblyOutput.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (StaticHelperPattern().IsMatch(lines[i]))
                {
                    // Check surrounding lines for type context
                    for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 3); j++)
                    {
                        var typeMatch = TypeCommentPattern().Match(lines[j]);
                        if (typeMatch.Success)
                        {
                            var typeName = NormalizeTypeName(typeMatch.Groups["type"].Value);
                            if (!string.IsNullOrEmpty(typeName) && IsValidTypeName(typeName))
                            {
                                detectedTypes.Add(typeName);
                            }
                        }

                        var fieldMatch = StaticFieldAccessPattern().Match(lines[j]);
                        if (fieldMatch.Success)
                        {
                            var typeName = NormalizeTypeName(fieldMatch.Groups["type"].Value);
                            if (!string.IsNullOrEmpty(typeName) && IsValidTypeName(typeName))
                            {
                                detectedTypes.Add(typeName);
                            }
                        }
                    }
                }
            }

            // Fallback: if helpers were found but no types extracted from nearby context
            // (common when JitDiffableDasm strips annotations or helper has no nearby type comment),
            // scan the entire method for types referenced in call instructions.
            // In diffable mode, long call targets wrap to the next line:
            //   call
            //   [Nethermind.Core.Eip4844Constants:get_MinBlobGasPrice():...]
            // Pre-running extra cctors is cheap; missing the right one means stale output.
            if (detectedTypes.Count == typesBeforeHelperScan)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    // Check current line and continuation lines after bare "call" instructions
                    if (line.TrimStart().StartsWith("call") || (i > 0 && lines[i - 1].TrimEnd().EndsWith("call")))
                    {
                        var callMatch = CallTargetTypePattern().Match(line);
                        if (callMatch.Success)
                        {
                            var typeName = NormalizeTypeName(callMatch.Groups["type"].Value);
                            if (!string.IsNullOrEmpty(typeName) && IsValidTypeName(typeName))
                            {
                                detectedTypes.Add(typeName);
                            }
                        }
                    }
                }
            }
        }

        var result = new List<string>(detectedTypes);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string NormalizeTypeName(string typeName)
    {
        // Remove generic arity suffix if present (e.g., `1, `2)
        var tickIndex = typeName.IndexOf('`');
        if (tickIndex > 0)
        {
            // Keep up to and including the backtick and number for proper type resolution
            var endIndex = tickIndex + 1;
            while (endIndex < typeName.Length && char.IsDigit(typeName[endIndex]))
            {
                endIndex++;
            }

            // If there's more after the generic arity, check for nested types
            if (endIndex < typeName.Length && typeName[endIndex] == '+')
            {
                // Keep nested type info
                return typeName;
            }

            return typeName[..endIndex];
        }

        return typeName.Trim();
    }

    private static bool IsValidTypeName(string typeName)
    {
        // Filter out obvious non-type names
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        // Must contain at least one letter
        if (!typeName.Any(char.IsLetter))
            return false;

        // Should not be just a keyword or register name
        var lowered = typeName.ToLowerInvariant();
        string[] invalidNames = ["rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rsp", "rbp",
                                 "eax", "ebx", "ecx", "edx", "esi", "edi", "esp", "ebp",
                                 "ptr", "dword", "qword", "byte", "word"];
        if (invalidNames.Contains(lowered))
            return false;

        return true;
    }
}

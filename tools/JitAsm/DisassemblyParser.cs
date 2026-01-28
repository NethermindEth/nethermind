// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.RegularExpressions;

namespace JitAsm;

internal static partial class DisassemblyParser
{
    // Pattern to detect the start of a method's disassembly
    // Example: ; Assembly listing for method Namespace.Type:Method(args)
    [GeneratedRegex(@"^; Assembly listing for method (?<method>.+)$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MethodHeaderPattern();

    // Pattern to detect end of method disassembly (next method or end)
    [GeneratedRegex(@"^; Total bytes of code", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MethodEndPattern();

    public static string Parse(string jitOutput)
    {
        if (string.IsNullOrWhiteSpace(jitOutput))
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        var matches = MethodHeaderPattern().Matches(jitOutput);

        if (matches.Count == 0)
        {
            // No method headers found, return raw output if it looks like assembly
            if (jitOutput.Contains("mov") || jitOutput.Contains("call") || jitOutput.Contains("ret"))
            {
                return jitOutput.Trim();
            }
            return string.Empty;
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var startIndex = match.Index;

            // Find the end of this method's disassembly
            int endIndex;
            if (i + 1 < matches.Count)
            {
                // End at the next method header
                endIndex = matches[i + 1].Index;
            }
            else
            {
                // End at the end of the output
                endIndex = jitOutput.Length;
            }

            // Extract this method's disassembly
            var methodAsm = jitOutput[startIndex..endIndex].TrimEnd();

            // Find "Total bytes of code" line and include it
            var totalBytesMatch = MethodEndPattern().Match(methodAsm);
            if (totalBytesMatch.Success)
            {
                // Find end of line after "Total bytes of code"
                var lineEnd = methodAsm.IndexOf('\n', totalBytesMatch.Index);
                if (lineEnd > 0)
                {
                    methodAsm = methodAsm[..(lineEnd + 1)].TrimEnd();
                }
            }

            if (result.Length > 0)
            {
                result.AppendLine();
                result.AppendLine(new string('-', 80));
                result.AppendLine();
            }

            result.AppendLine(methodAsm);
        }

        return result.ToString().Trim();
    }

    public static IEnumerable<MethodDisassembly> ParseMethods(string jitOutput)
    {
        if (string.IsNullOrWhiteSpace(jitOutput))
        {
            yield break;
        }

        var matches = MethodHeaderPattern().Matches(jitOutput);

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var methodName = match.Groups["method"].Value;
            var startIndex = match.Index;

            int endIndex;
            if (i + 1 < matches.Count)
            {
                endIndex = matches[i + 1].Index;
            }
            else
            {
                endIndex = jitOutput.Length;
            }

            var methodAsm = jitOutput[startIndex..endIndex].TrimEnd();

            yield return new MethodDisassembly
            {
                MethodName = methodName,
                Assembly = methodAsm
            };
        }
    }
}

internal sealed class MethodDisassembly
{
    public required string MethodName { get; init; }
    public required string Assembly { get; init; }
}

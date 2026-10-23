// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.RegularExpressions;

internal static class VerifierArtifacts
{
    internal static int Check(string root, IEnumerable<string> labels)
    {
        var failures = new List<string>();
        foreach (var label in labels)
        {
            var sweep = "sweep-" + label;
            var verifier = sweep + "/verifier.hex";
            var code = ReadHex(verifier);
            if (code is not null)
            {
                if (code.Length is < 1024 or > 24576) Reject(verifier, "size", $"{code.Length} bytes, outside 1024..24576 (EIP-170)");
                var pushed = new HashSet<byte>();
                var staticCall = false;
                for (var pc = 0; pc < code.Length;)
                {
                    var op = code[pc];
                    if (op == 0x60 && pc + 1 < code.Length) pushed.Add(code[pc + 1]);
                    staticCall |= op == 0xfa;
                    pc += 1 + (op is >= 0x60 and <= 0x7f ? op - 0x60 + 1 : 0);
                }
                var missing = new List<string>();
                foreach (var (address, name) in new[] { (6, "ecAdd"), (7, "ecMul"), (8, "ecPairing") })
                    if (!pushed.Contains((byte)address)) missing.Add($"0x{address:x2} {name}");
                if (missing.Count > 0) Reject(verifier, "precompile-push", "no PUSH1 of " + string.Join(", ", missing));
                if (!staticCall) Reject(verifier, "staticcall", "no STATICCALL opcode");
            }
            var calldata = sweep + "/calldata-invalid.hex";
            if (ReadHex(calldata) is { Length: < 4 }) Reject(calldata, "length", "shorter than a function selector");
            if (!Regex.IsMatch(Read(sweep + "/gas.txt"), @"\A[1-9][0-9]{0,8}\z")) Reject(sweep + "/gas.txt", "integer", "not a positive integer");
        }
        foreach (var failure in failures) Console.WriteLine("::error::" + failure);
        return failures.Count > 0 ? 1 : 0;

        void Reject(string path, string check, string detail) => failures.Add($"CheckVerifiers.cs heuristic rejected {path} ({check} check): {detail}");
        string Read(string path) => Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(root, path))).Trim();
        byte[]? ReadHex(string path)
        {
            var text = Read(path);
            if (Regex.IsMatch(text, @"\A0x(?:[0-9a-fA-F]{2})+\z")) return Convert.FromHexString(text[2..]);
            Reject(path, "hex", "not 0x-prefixed, byte-aligned hex");
            return null;
        }
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static class BenchmarkState
{
    internal static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0 || !Regex.IsMatch(line[..separator], "^[A-Za-z_][A-Za-z0-9_]*$")) throw new IOException("Invalid benchmark state assignment");
            result[line[..separator]] = Decode(line[(separator + 1)..]);
        }
        return result;
    }

    internal static void Append(string path, string key, string value)
    {
        if (!Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new ArgumentException("Invalid state key", nameof(key));
        File.AppendAllText(path, key + "=" + Encode(value) + "\n");
    }

    internal static string Encode(string value)
    {
        if (value.Length == 0) return "''";
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsControl(character)) throw new ArgumentException("Control characters are not supported in benchmark state", nameof(value));
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('/' or '_' or '-' or '.' or ':' or '@' or '=' or ',')) builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

    internal static string Decode(string value)
    {
        var builder = new StringBuilder();
        var quote = '\0';
        var ansi = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote == '\0' && character == '$' && index + 1 < value.Length && value[index + 1] == '\'')
            {
                quote = '\''; ansi = true; index++; continue;
            }
            if (character == '\\' && (quote != '\'' || ansi))
            {
                if (++index >= value.Length) throw new IOException("Truncated state escape");
                var escaped = value[index];
                if (ansi)
                {
                    if (escaped is >= '0' and <= '7')
                    {
                        var octal = escaped.ToString();
                        for (var count = 1; count < 3 && index + 1 < value.Length && value[index + 1] is >= '0' and <= '7'; count++) octal += value[++index];
                        builder.Append((char)Convert.ToInt32(octal, 8)); continue;
                    }
                    if (escaped is 'x' or 'u' or 'U')
                    {
                        var maximum = escaped == 'x' ? 2 : escaped == 'u' ? 4 : 8;
                        var hex = "";
                        while (hex.Length < maximum && index + 1 < value.Length && char.IsAsciiHexDigit(value[index + 1])) hex += value[++index];
                        if (hex.Length == 0) throw new IOException("Invalid hexadecimal state escape");
                        builder.Append(char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture))); continue;
                    }
                    builder.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', 'e' or 'E' => '\u001b', _ => escaped });
                }
                else builder.Append(escaped);
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote) { quote = '\0'; ansi = false; }
                else
                {
                    if (quote == '"' && character is '$' or '`') throw new IOException("Expansion is not allowed in benchmark state");
                    builder.Append(character);
                }
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (char.IsWhiteSpace(character) || character is '$' or '`' or ';' or '|' or '&' or '<' or '>' or '(' or ')') throw new IOException("Executable expressions are not allowed in benchmark state");
            builder.Append(character);
        }
        if (quote != '\0') throw new IOException("Unterminated state quote");
        return builder.ToString();
    }
}

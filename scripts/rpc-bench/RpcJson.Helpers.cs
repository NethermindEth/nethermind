// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class RpcJson
{
    internal static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(", ", value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => Quote(property.Name) + ": " + Canonical(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => Quote(value.GetString()!),
        JsonValueKind.Number => Number(value),
        _ => value.GetRawText()
    };

    static string Number(JsonElement value)
    {
        var raw = value.GetRawText();
        if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')) return raw;
        var number = value.GetDouble();
        var text = number.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        if (text.Contains('e')) return text;
        if (Math.Abs(number) >= 1e16)
        {
            var negative = text.StartsWith('-');
            var digits = negative ? text[1..] : text;
            var exponent = (digits.Contains('.') ? digits.IndexOf('.') : digits.Length) - 1;
            digits = digits.Replace(".", "", StringComparison.Ordinal).TrimEnd('0');
            return (negative ? "-" : "") + digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e+" + exponent.ToString("D2", CultureInfo.InvariantCulture);
        }
        return text.Contains('.') ? text : text + ".0";
    }

    static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in text)
            builder.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' or > '~' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        return builder.Append('"').ToString();
    }
}

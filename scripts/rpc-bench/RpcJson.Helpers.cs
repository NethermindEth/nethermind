// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class RpcJson
{
    internal static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 1024 };
    internal static readonly JsonSerializerOptions SerializerOptions = new() { MaxDepth = 1024 };

    internal static string Canonical(JsonElement value, bool compact = false) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(compact ? "," : ", ", value.EnumerateObject().OrderBy(property => property.Name, CodePointOrder).Select(property => Quote(property.Name) + (compact ? ":" : ": ") + Canonical(property.Value, compact))) + "}",
        JsonValueKind.Array => "[" + string.Join(compact ? "," : ", ", value.EnumerateArray().Select(item => Canonical(item, compact))) + "]",
        JsonValueKind.String => Quote(value.GetString()!),
        JsonValueKind.Number => Number(value),
        _ => value.GetRawText()
    };

    private static readonly Comparer<string> CodePointOrder = Comparer<string>.Create((left, right) =>
    {
        var a = left.EnumerateRunes(); var b = right.EnumerateRunes();
        while (a.MoveNext())
        {
            if (!b.MoveNext()) return 1;
            var comparison = a.Current.Value.CompareTo(b.Current.Value);
            if (comparison != 0) return comparison;
        }
        return b.MoveNext() ? -1 : 0;
    });

    static string Number(JsonElement value)
    {
        var raw = value.GetRawText();
        if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')) return raw == "-0" ? "0" : raw;
        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new JsonException("Non-finite JSON number");
        var text = number.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        if (text.Contains('e'))
        {
            if (Math.Abs(number) is < 1e-4 or >= 1e16) return text;
            text = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
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

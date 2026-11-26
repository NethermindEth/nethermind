// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Makes untrusted text (on-chain strings, node error messages) safe to show to an LLM client.</summary>
/// <remarks>
/// Works on Unicode scalar values (<see cref="Rune"/>s), not UTF-16 units, so characters outside the Basic Multilingual Plane are
/// classified correctly and a cut never splits a surrogate pair. Removed: control characters (optionally replaced by a space),
/// format characters (bidi overrides and isolates, zero-width joiners and spaces, and the Unicode TAG block U+E0000..U+E007F used
/// to smuggle invisible instructions), private-use, surrogate and unassigned code points, variation selectors
/// (U+FE00..U+FE0F, U+E0100..U+E01EF) and the U+FFFD replacement character left by invalid UTF-8. Visible text, including emoji,
/// is kept.
/// </remarks>
internal static class McpText
{
    /// <summary>The maximum length of an ABI-decoded <c>string</c> value in tool output.</summary>
    public const int MaxDecodedStringLength = 1024;

    /// <summary>The marker appended to text cut at its maximum length.</summary>
    public const string TruncationMarker = "…[truncated]";

    /// <summary>Returns <paramref name="text"/> without unsafe characters and at most <paramref name="maxLength"/> UTF-16 units long.</summary>
    /// <param name="text">The untrusted text.</param>
    /// <param name="maxLength">The maximum length of the kept text in UTF-16 units, excluding <paramref name="truncationMarker"/>.</param>
    /// <param name="controlsAsSpace">Whether control characters (such as line breaks) become spaces instead of being removed.</param>
    /// <param name="truncationMarker">Appended when the text was cut; <see langword="null"/> for none.</param>
    public static string Sanitize(string text, int maxLength, bool controlsAsSpace = false, string? truncationMarker = null)
    {
        StringBuilder builder = new(Math.Min(text.Length, maxLength));
        Span<char> units = stackalloc char[2];
        bool truncated = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            Rune kept = rune;
            if (IsUnsafe(rune))
            {
                if (!controlsAsSpace || Rune.GetUnicodeCategory(rune) != UnicodeCategory.Control)
                {
                    continue;
                }

                kept = new Rune(' ');
            }

            if (builder.Length + kept.Utf16SequenceLength > maxLength)
            {
                truncated = true;
                break;
            }

            builder.Append(units[..kept.EncodeToUtf16(units)]);
        }

        if (truncated && truncationMarker is not null)
        {
            builder.Append(truncationMarker);
        }

        return builder.ToString();
    }

    /// <summary>Returns whether <paramref name="rune"/> must not reach a client verbatim from untrusted text.</summary>
    public static bool IsUnsafe(Rune rune)
    {
        int value = rune.Value;
        if (value is >= 0xFE00 and <= 0xFE0F or >= 0xE0000 and <= 0xE01EF or 0xFFFD)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse
            or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned;
    }
}

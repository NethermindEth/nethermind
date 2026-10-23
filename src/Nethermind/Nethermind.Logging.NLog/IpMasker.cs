// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Text.RegularExpressions;

namespace Nethermind.Logging.NLog;

/// <summary>
/// Hides IP addresses, enode URLs and ENR records in log messages, so that users can share their logs.
/// </summary>
/// <remarks>Loopback and unspecified addresses stay visible, as they do not identify a node.</remarks>
public static partial class IpMasker
{
    public const string MaskedIpv4 = "x.x.x.x";
    public const string MaskedIpv6 = "x::x";
    public const string MaskedEnode = "enode://***@***";
    public const string MaskedEnr = "enr:***";

    private static readonly MatchEvaluator s_evaluator = MaskMatch;

    public static bool Enabled { get; set; }

    public static string MaskIfEnabled(string text) => Enabled ? Mask(text) : text;

    public static string Mask(string text) =>
        text.AsSpan().ContainsAny('.', ':') ? SensitiveData().Replace(text, s_evaluator) : text;

    private static string MaskMatch(Match match)
    {
        if (match.Groups["enode"].Success) return MaskedEnode;
        if (match.Groups["enr"].Success) return MaskedEnr;

        ReadOnlySpan<char> value = match.ValueSpan;
        if (IsMaskable(value, out bool isIpv6)) return isIpv6 ? MaskedIpv6 : MaskedIpv4;

        // A trailing colon is punctuation, as in "fe80::1: message".
        return isIpv6 && value.EndsWith(':') && !value.EndsWith("::") && IsMaskable(value[..^1], out _)
            ? MaskedIpv6 + ":"
            : match.Value;
    }

    private static bool IsMaskable(ReadOnlySpan<char> value, out bool isIpv6)
    {
        isIpv6 = value.Contains(':');
        if (!IPAddress.TryParse(value, out IPAddress? address)) return false;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
    }

    [GeneratedRegex("""
        (?<enode>enode://[0-9A-Fa-f]*(?:@(?:\[[^\]\s]*\]|[\w.-]*))?)
        |(?<enr>enr:-[\w-]+)
        |(?<![\w.])\d{1,3}(?:\.\d{1,3}){3}(?!\.?\w)
        |(?<![\w.])(?:[0-9A-Fa-f]{1,4})?(?::[0-9A-Fa-f]{0,4}){2,7}(?:(?:\.\d{1,3}){3})?(?:%\w+)?(?![\w.])
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveData();
}

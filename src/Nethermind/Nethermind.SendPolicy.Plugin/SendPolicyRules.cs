// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.SendPolicy.Plugin;

/// <summary>
/// One parsed state of the rule file.
/// </summary>
/// <remarks>
/// One rule per line, <c>#</c> starts a comment:
/// <list type="bullet">
/// <item><c>from &lt;address&gt;</c>: a guarded sender. Senders not listed are never checked.</item>
/// <item><c>to &lt;address&gt; [&lt;max wei per transaction&gt;]</c>: a destination a guarded sender may call; the limit defaults to zero.</item>
/// <item><c>grant &lt;address&gt;</c>: an address that may receive tokens, an allowance or a guarded account's EIP-7702 delegation.</item>
/// <item><c>fee &lt;max wei&gt;</c>: the ceiling on gas limit times max fee per gas.</item>
/// </list>
/// </remarks>
public sealed class SendPolicyRules
{
    public static readonly SendPolicyRules Empty = new([], [], [], null, null);

    private SendPolicyRules(
        HashSet<Address> guardedSenders,
        Dictionary<Address, (UInt256 MaxValue, int Line)> destinations,
        HashSet<Address> grants,
        UInt256? maxFee,
        string? error)
    {
        GuardedSenders = guardedSenders;
        Destinations = destinations;
        Grants = grants;
        MaxFee = maxFee;
        Error = error;
    }

    public IReadOnlySet<Address> GuardedSenders { get; }
    public IReadOnlyDictionary<Address, (UInt256 MaxValue, int Line)> Destinations { get; }
    public IReadOnlySet<Address> Grants { get; }
    public UInt256? MaxFee { get; }

    /// <summary>
    /// Why the file could not be used, or <c>null</c> when it was parsed.
    /// </summary>
    /// <remarks>
    /// An unusable file keeps the guarded senders of the last good state and refuses everything they send.
    /// </remarks>
    public string? Error { get; }

    /// <exception cref="FormatException">A line has an unknown rule, a wrong argument count or a malformed value.</exception>
    public static SendPolicyRules Parse(IEnumerable<string> lines)
    {
        HashSet<Address> guardedSenders = [];
        Dictionary<Address, (UInt256 MaxValue, int Line)> destinations = [];
        HashSet<Address> grants = [];
        UInt256? maxFee = null;
        int lineNumber = 0;
        foreach (string raw in lines)
        {
            lineNumber++;
            string[] words = raw.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            try
            {
                switch (words[0], words.Length)
                {
                    case ("from", 2): guardedSenders.Add(new Address(words[1])); break;
                    case ("to", 2): destinations[new Address(words[1])] = (UInt256.Zero, lineNumber); break;
                    case ("to", 3): destinations[new Address(words[1])] = (ParseWei(words[2]), lineNumber); break;
                    case ("grant", 2): grants.Add(new Address(words[1])); break;
                    case ("fee", 2): maxFee = ParseWei(words[1]); break;
                    default: throw new FormatException($"unknown rule '{words[0]}' with {words.Length - 1} argument(s)");
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
            {
                throw new FormatException($"line {lineNumber}: {e.Message}", e);
            }
        }

        return new SendPolicyRules(guardedSenders, destinations, grants, maxFee, null);
    }

    public static SendPolicyRules Unusable(SendPolicyRules lastGood, string reason) =>
        new([.. lastGood.GuardedSenders], [], [], null, reason);

    private static UInt256 ParseWei(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) ? UInt256.Parse(value) : throw new FormatException($"'{value}' is not a decimal wei amount");
}

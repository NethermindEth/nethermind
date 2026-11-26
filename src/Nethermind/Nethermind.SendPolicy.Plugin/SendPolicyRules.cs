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
/// <item><c>to &lt;address&gt; [&lt;max wei per transaction&gt;]</c>: a destination a guarded sender may call with any calldata; the limit defaults to zero.</item>
/// <item><c>token &lt;address&gt;</c>: a destination where a guarded sender may make only the token calls that <see cref="SendPolicyCheck"/> decodes, with no value.</item>
/// <item><c>grant &lt;address&gt;</c>: an address that may receive tokens, an allowance or a guarded account's EIP-7702 delegation.</item>
/// <item><c>fee &lt;max wei&gt;</c>: the ceiling on gas limit times max fee per gas.</item>
/// <item><c>cap &lt;max wei&gt; &lt;seconds&gt;</c>: the most value plus fee ceiling one guarded sender may submit through this node in any period of that length.</item>
/// </list>
/// An address may have one <c>to</c> or <c>token</c> rule, and the file one <c>fee</c> and one <c>cap</c> rule.
/// </remarks>
public sealed class SendPolicyRules
{
    public static readonly SendPolicyRules Empty = new([], [], [], null, null, null);

    private SendPolicyRules(
        HashSet<Address> guardedSenders,
        Dictionary<Address, (UInt256 MaxValue, int Line, bool TokenOnly)> destinations,
        HashSet<Address> grants,
        UInt256? maxFee,
        (UInt256 MaxWei, ulong Seconds)? cap,
        string? error)
    {
        GuardedSenders = guardedSenders;
        Destinations = destinations;
        Grants = grants;
        MaxFee = maxFee;
        Cap = cap;
        Error = error;
    }

    public IReadOnlySet<Address> GuardedSenders { get; }
    public IReadOnlyDictionary<Address, (UInt256 MaxValue, int Line, bool TokenOnly)> Destinations { get; }
    public IReadOnlySet<Address> Grants { get; }
    public UInt256? MaxFee { get; }
    public (UInt256 MaxWei, ulong Seconds)? Cap { get; }

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
        Dictionary<Address, (UInt256 MaxValue, int Line, bool TokenOnly)> destinations = [];
        HashSet<Address> grants = [];
        UInt256? maxFee = null;
        (UInt256 MaxWei, ulong Seconds)? cap = null;
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
                    case ("to", 2): AddDestination(destinations, words[1], UInt256.Zero, lineNumber, false); break;
                    case ("to", 3): AddDestination(destinations, words[1], ParseWei(words[2]), lineNumber, false); break;
                    case ("token", 2): AddDestination(destinations, words[1], UInt256.Zero, lineNumber, true); break;
                    case ("grant", 2): grants.Add(new Address(words[1])); break;
                    case ("fee", 2): maxFee = maxFee is null ? ParseWei(words[1]) : throw new FormatException("a second 'fee' rule"); break;
                    case ("cap", 3): cap = cap is null ? (ParseWei(words[1]), ParseSeconds(words[2])) : throw new FormatException("a second 'cap' rule"); break;
                    default: throw new FormatException($"unknown rule '{words[0]}' with {words.Length - 1} argument(s)");
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
            {
                throw new FormatException($"line {lineNumber}: {e.Message}", e);
            }
        }

        return new SendPolicyRules(guardedSenders, destinations, grants, maxFee, cap, null);
    }

    public static SendPolicyRules Unusable(SendPolicyRules lastGood, string reason) =>
        new([.. lastGood.GuardedSenders], [], [], null, null, reason);

    private static void AddDestination(Dictionary<Address, (UInt256 MaxValue, int Line, bool TokenOnly)> destinations, string address, UInt256 maxValue, int line, bool tokenOnly)
    {
        Address destination = new(address);
        if (!destinations.TryAdd(destination, (maxValue, line, tokenOnly)))
            throw new FormatException($"{destination} already has a rule at line {destinations[destination].Line}");
    }

    private static UInt256 ParseWei(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) ? UInt256.Parse(value) : throw new FormatException($"'{value}' is not a decimal wei amount");

    private static ulong ParseSeconds(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) && ulong.Parse(value) > 0 ? ulong.Parse(value) : throw new FormatException($"'{value}' is not a positive number of seconds");
}

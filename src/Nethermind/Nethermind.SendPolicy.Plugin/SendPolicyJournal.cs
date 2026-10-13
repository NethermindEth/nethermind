// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.SendPolicy.Plugin;

/// <summary>
/// Remembers what guarded senders submitted through this node, so that a <c>cap</c> rule can be checked.
/// </summary>
/// <remarks>
/// One line per accepted transaction: <c>&lt;unix seconds&gt; &lt;sender&gt; &lt;nonce&gt; &lt;max outflow wei&gt;</c>.
/// Transactions of one sender with the same nonce count once, at the largest of their outflows, because at most
/// one of them can be included. Transactions submitted elsewhere are not known. Nothing is recorded while the
/// rules have no <c>cap</c>, and entries older than the cap period are dropped, so a cap added or lengthened
/// later counts only what is still kept. Safe to call from concurrent JSON-RPC requests.
/// </remarks>
public sealed class SendPolicyJournal
{
    private readonly string _path;
    private readonly ITimestamper _timestamper;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<Entry> _committed = [];
    private readonly List<Entry> _reserved = [];
    private int _staleLines;
    private string? _error;

    public SendPolicyJournal(ISendPolicyConfig config, ITimestamper timestamper, ILogManager logManager)
    {
        _path = config.JournalPath ?? config.RulesPath + ".journal";
        _timestamper = timestamper;
        _logger = logManager.GetClassLogger<SendPolicyJournal>();
        try
        {
            if (File.Exists(_path)) _committed.AddRange(File.ReadLines(_path).Where(static line => line.Length > 0).Select(Entry.Parse));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException or OverflowException)
        {
            Fail(e);
        }
    }

    /// <summary>
    /// Counts a transaction that is about to be submitted, unless it would pass the cap.
    /// </summary>
    /// <param name="nonce">The nonce, or <c>null</c> when the node has not assigned it yet.</param>
    /// <param name="enforce">Whether a transaction over the cap is left uncounted.</param>
    /// <param name="entry">The counted transaction, to pass to <see cref="Commit"/> or <see cref="Release"/>; <c>null</c> when nothing was counted.</param>
    /// <returns>The reason the cap refuses the transaction, or <c>null</c>.</returns>
    public string? Reserve(Address sender, ulong? nonce, UInt256 outflow, (UInt256 MaxWei, ulong Seconds)? cap, bool enforce, out Entry? entry)
    {
        entry = null;
        if (cap is not { } limit) return null;
        ulong now = _timestamper.UnixTime.Seconds;
        ulong since = now > limit.Seconds ? now - limit.Seconds : 0;
        lock (_lock)
        {
            if (_error is null) Forget(since);
            string? refusal = null;
            if (_error is not null) refusal = $"journal unusable ({_error})";
            else if (Submitted(sender, since, nonce, outflow) is { } submitted && submitted > limit.MaxWei)
                refusal = $"over 'cap {limit.MaxWei} {limit.Seconds}': {submitted} wei with this transaction";

            if (refusal is null || !enforce) _reserved.Add(entry = new Entry(now, sender, nonce, outflow));
            return refusal;
        }
    }

    public void Commit(Entry entry, ulong nonce)
    {
        Entry committed = entry with { Nonce = nonce };
        lock (_lock)
        {
            _reserved.Remove(entry);
            _committed.Add(committed);
            try
            {
                File.AppendAllLines(_path, [committed.ToString()]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Fail(e);
            }
        }
    }

    public void Release(Entry entry)
    {
        lock (_lock) _reserved.Remove(entry);
    }

    private void Forget(ulong since)
    {
        _staleLines += _committed.RemoveAll(entry => entry.Time < since);
        if (_staleLines <= _committed.Count) return;
        try
        {
            string rewritten = _path + ".tmp";
            File.WriteAllLines(rewritten, _committed.Select(static entry => entry.ToString()));
            File.Move(rewritten, _path, overwrite: true);
            _staleLines = 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(e);
        }
    }

    private UInt256 Submitted(Address sender, ulong since, ulong? nonce, UInt256 outflow)
    {
        Dictionary<ulong, UInt256> byNonce = [];
        UInt256 total = UInt256.Zero;
        if (nonce is { } own) byNonce[own] = outflow;
        else total = outflow;
        foreach (Entry entry in _committed.Concat(_reserved))
        {
            if (entry.Time < since || entry.Sender != sender) continue;
            if (entry.Nonce is not { } entryNonce) total = Sum(total, entry.Outflow);
            else if (!byNonce.TryGetValue(entryNonce, out UInt256 known) || entry.Outflow > known) byNonce[entryNonce] = entry.Outflow;
        }

        foreach (UInt256 value in byNonce.Values) total = Sum(total, value);
        return total;
    }

    private static UInt256 Sum(in UInt256 a, in UInt256 b) => UInt256.AddOverflow(a, b, out UInt256 sum) ? UInt256.MaxValue : sum;

    private void Fail(Exception e)
    {
        _error = e.Message;
        if (_logger.IsError) _logger.Error($"SendPolicy journal at {_path}: {e.Message}; refusing every transaction a cap rule applies to");
    }

    public sealed record Entry(ulong Time, Address Sender, ulong? Nonce, UInt256 Outflow)
    {
        public static Entry Parse(string line)
        {
            string[] words = line.Split(' ');
            if (words.Length != 4) throw new FormatException($"journal line '{line}' does not have 4 fields");
            return new Entry(ulong.Parse(words[0], CultureInfo.InvariantCulture), new Address(words[1]), ulong.Parse(words[2], CultureInfo.InvariantCulture), UInt256.Parse(words[3]));
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Time} {Sender} {Nonce} {Outflow}");
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.Dns;

public class EnrTreeCrawler
{
    internal const int MaxQueries = 16384;
    internal const int MaxRecords = 4096;
    private const int MaxDepth = 64;
    private const int MaxTrees = 32;
    private readonly ILogger _logger;
    private readonly Lock _sequenceLock = new();
    private readonly LruCache<string, ulong> _sequences = new(MaxTrees, "DNS tree sequences");
    private readonly Func<string, IDnsClient> _clientFactory;
    private readonly TimeSpan _crawlTimeout;
    private readonly TimeProvider _timeProvider;

    public EnrTreeCrawler(ILogger logger)
        : this(logger, static domain => new DnsClient(domain))
    {
    }

    internal EnrTreeCrawler(ILogger logger, Func<string, IDnsClient> clientFactory,
        TimeProvider? timeProvider = null, TimeSpan? crawlTimeout = null)
    {
        _logger = logger;
        _clientFactory = clientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _crawlTimeout = crawlTimeout ?? TimeSpan.FromMinutes(5);
    }

    public async IAsyncEnumerable<string> SearchTree(string domain, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CrawlState? state = CreateCrawl(domain);
        if (state is null) yield break;
        await foreach (string record in SearchTree(state, cancellationToken)) yield return record;
    }

    internal CrawlState? CreateCrawl(string domain) => TryCreateTree(domain, out Tree? tree) ? new(tree) : null;

    private bool TryCreateTree(string address, [NotNullWhen(true)] out Tree? tree, bool requireSigner = false)
    {
        tree = null;
        if (address.StartsWith("enrtree://", StringComparison.OrdinalIgnoreCase)) address = address[10..];
        byte[]? signer = null;
        int separator = address.IndexOf('@');
        if (separator >= 0)
        {
            string key = address[..separator].ToUpperInvariant();
            signer = new byte[CompressedPublicKey.LengthInBytes];
            if (!EnrTreeHash.TryDecodeBase32(key, signer, out int length) || length != signer.Length || signer[0] is not (2 or 3))
            {
                if (requireSigner)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping DNS tree link: '{key}' is not a base32 compressed public key of the ENR tree signer.");
                }
                else if (_logger.IsError) _logger.Error($"Skipping DNS discovery: '{key}' is not a base32 compressed public key of the ENR tree signer.");
                return false;
            }
            address = address[(separator + 1)..];
        }
        string domain = address.TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns)
        {
            if (!requireSigner && _logger.IsError) _logger.Error($"Invalid DNS discovery domain '{domain}'.");
            return false;
        }
        if (requireSigner && signer is null) return false;
        if (signer is null && _logger.IsWarn)
            _logger.Warn($"No ENR tree signer public key configured for '{domain}', the tree root signature will not be verified. Use enrtree://<public key>@<domain>.");
        tree = new Tree(_clientFactory(domain), signer, domain + "@" + (signer is null ? "" : Convert.ToHexString(signer)));
        return true;
    }

    internal async IAsyncEnumerable<string> SearchTree(CrawlState state, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        state.Paused = false;
        state.Restart = false;
        using CancellationTokenSource deadline = new(_crawlTimeout, _timeProvider);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Queue<PendingQuery> pending = state.Pending;
        HashSet<(string Tree, string Reference)> scheduled = state.Scheduled;
        HashSet<string> trees = state.Trees;
        HashSet<string> records = state.Records;
        bool truncated = false;
        while (pending.TryPeek(out PendingQuery? item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
            {
                Truncate("deadline");
                state.Paused = state.CanResume;
                yield break;
            }
            if (state.Work >= MaxQueries)
            {
                Truncate("query cap");
                yield break;
            }
            IEnumerable<string> answers;
            try
            {
                state.Queries++;
                answers = await item.Tree.Client.Lookup(item.Query, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug($"DNS query '{item.Query}' failed: {ex.Message}");
                state.FailPending();
                if (state.Restart && !timeout.IsCancellationRequested)
                {
                    state.Restart = false;
                    continue;
                }
                Truncate(timeout.IsCancellationRequested ? "deadline" : "resolver error");
                yield break;
            }

            foreach (string text in answers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Examined == MaxQueries || timeout.IsCancellationRequested)
                {
                    Truncate(timeout.IsCancellationRequested ? "deadline" : "answer cap");
                    state.Paused = timeout.IsCancellationRequested && state.CanResume;
                    yield break;
                }
                state.Examined++;
                // A 300-byte ENR occupies at most 400 base64url characters after the prefix.
                if (text.Length > 4096 || text.Length > 404 && text.StartsWith("enr:", StringComparison.Ordinal)) continue;
                if (item.Query.Length != 0 && !EnrTreeHash.Matches(item.Query, text))
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting ENR tree record from DNS query '{item.Query}': content does not hash to the subdomain it was served from.");
                    continue;
                }
                EnrTreeNode node;
                try
                {
                    node = EnrTreeParser.ParseNode(text);
                }
                catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping malformed ENR tree record: {ex.Message}");
                    continue;
                }
                if (item.Query.Length == 0)
                {
                    if (node is not EnrTreeRoot root || (item.Tree.Signer is not null && !root.IsSignedBy(item.Tree.Signer)))
                    {
                        if (_logger.IsWarn) _logger.Warn($"Rejecting ENR tree root '{text}': it is not an enrtree-root signed by the configured tree signer.");
                        continue;
                    }
                    if (!AcceptSequence(item.Tree.Identity, root.Sequence)) continue;
                }
                foreach (string link in node.Links)
                {
                    if (trees.Count < MaxTrees && item.Depth < MaxDepth && TryCreateTree(link, out Tree? linked, requireSigner: true) && trees.Add(linked.Identity))
                        Enqueue(linked, string.Empty, item.Depth + 1);
                }
                foreach (string record in node.Records)
                {
                    if (!records.Add(record)) continue;
                    yield return record;
                    if (records.Count == MaxRecords)
                    {
                        Truncate("record cap");
                        yield break;
                    }
                }
                foreach (string reference in node.Refs) Enqueue(item.Tree, reference, item.Depth + 1);
            }
            pending.Dequeue();
        }


        void Enqueue(Tree tree, string query, int depth)
        {
            if (depth > MaxDepth)
            {
                Truncate("depth cap");
                return;
            }
            string canonical = string.Empty;
            if (query.Length != 0)
            {
                query = query.ToUpperInvariant();
                Span<byte> hash = stackalloc byte[32];
                if (!EnrTreeHash.TryDecodeBase32(query, hash, out int length) || length < 12) return;
                canonical = Convert.ToHexString(hash[..12]);
            }
            if (scheduled.Contains((tree.Identity, canonical))) return;
            if (scheduled.Count >= MaxQueries)
            {
                Truncate("query cap");
                return;
            }
            if (scheduled.Add((tree.Identity, canonical))) pending.Enqueue(new(tree, query, depth));
        }

        void Truncate(string reason)
        {
            if (truncated) return;
            truncated = true;
            if (_logger.IsWarn) _logger.Warn($"DNS discovery crawl truncated by {reason}: {state.Queries} queries, {state.Examined} answers, {state.Exceptions} exceptional recovery units, {records.Count} records, {scheduled.Count} scheduled queries.");
        }
    }

    private bool AcceptSequence(string identity, ulong sequence)
    {
        lock (_sequenceLock)
        {
            if (_sequences.TryGet(identity, out ulong previous))
            {
                if (sequence < previous) return false;
            }
            _sequences.Set(identity, sequence);
            return true;
        }
    }

    internal sealed class CrawlState
    {
        internal readonly Queue<PendingQuery> Pending = new();
        internal readonly HashSet<(string Tree, string Reference)> Scheduled = [];
        internal readonly HashSet<string> Trees;
        internal readonly HashSet<string> Records = [with(StringComparer.Ordinal)];
        internal int Queries;
        internal int Examined;
        internal bool Paused;
        internal bool Restart;
        internal int Exceptions;
        internal int Work => Queries + Exceptions;
        internal bool CanResume => Pending.Count != 0 && Work < MaxQueries && Examined < MaxQueries && Records.Count < MaxRecords;

        internal void FailPending()
        {
            Paused = false;
            Restart = false;
            if (Pending.TryPeek(out PendingQuery? item) && item.Depth != 0 && ++item.Failures >= 3)
            {
                Pending.Dequeue();
                Restart = CanResume;
            }
            else Paused = CanResume;
        }

        internal void FailEscaping()
        {
            if (Work < MaxQueries) Exceptions++;
            FailPending();
        }

        internal CrawlState(Tree tree)
        {
            Trees = [tree.Identity];
            Scheduled.Add((tree.Identity, string.Empty));
            Pending.Enqueue(new(tree, string.Empty, 0));
        }
    }

    internal sealed class PendingQuery(Tree tree, string query, int depth)
    {
        internal Tree Tree { get; } = tree;
        internal string Query { get; } = query;
        internal int Depth { get; } = depth;
        internal int Failures;
    }

    internal sealed record Tree(IDnsClient Client, byte[]? Signer, string Identity);
}

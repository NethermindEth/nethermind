// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.Dns;

public class EnrTreeCrawler(ILogger logger)
{
    private readonly ILogger _logger = logger;

    public IAsyncEnumerable<string> SearchTree(string domain, CancellationToken cancellationToken = default)
    {
        byte[]? signerPublicKey = null;
        if (domain.StartsWith("enrtree://", StringComparison.OrdinalIgnoreCase))
        {
            domain = domain[10..];
            string[] pubkey_and_url = domain.Split("@");
            if (pubkey_and_url.Length > 1)
            {
                signerPublicKey = new byte[CompressedPublicKey.LengthInBytes];
                if (!EnrTreeHash.TryDecodeBase32(pubkey_and_url[0], signerPublicKey, out int keyLength) || keyLength != signerPublicKey.Length)
                {
                    if (_logger.IsError) _logger.Error($"Skipping DNS discovery: '{pubkey_and_url[0]}' is not a base32 compressed public key of the ENR tree signer.");
                    return AsyncEnumerable.Empty<string>();
                }

                domain = pubkey_and_url[1];
            }
        }

        if (signerPublicKey is null && _logger.IsWarn)
        {
            _logger.Warn($"No ENR tree signer public key configured for '{domain}', the tree root signature will not be verified. Use enrtree://<public key>@<domain>.");
        }

        DnsClient client = new(domain);
        return SearchTree(client, signerPublicKey, cancellationToken);
    }

    internal IAsyncEnumerable<string> SearchTree(IDnsClient client, byte[]? signerPublicKey = null, CancellationToken cancellationToken = default) =>
        SearchTree(client, new SearchContext(string.Empty, signerPublicKey), cancellationToken);

    private async IAsyncEnumerable<string> SearchTree(IDnsClient client, SearchContext searchContext, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (searchContext.RefsToVisit.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string reference = searchContext.RefsToVisit.Dequeue();
            await foreach (string nodeRecordText in SearchNode(client, reference, searchContext, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return nodeRecordText;
            }
        }
    }

    private async IAsyncEnumerable<string> SearchNode(IDnsClient client, string query, SearchContext searchContext, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (searchContext.VisitedRefs.Add(query))
        {
            IEnumerable<string> lookupResult = await client.Lookup(query, cancellationToken);
            foreach (string node in lookupResult)
            {
                // An empty query is a tree root, which EIP-1459 serves from a bare domain with no hash label.
                if (query.Length != 0 && !EnrTreeHash.Matches(query, node))
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting ENR tree record from DNS query '{query}': content does not hash to the subdomain it was served from.");
                    continue;
                }

                EnrTreeNode treeNode;
                try
                {
                    treeNode = EnrTreeParser.ParseNode(node);
                }
                catch (Exception e) when (e is FormatException or NotSupportedException)
                {
                    // A single malformed record from an untrusted DNS server must not abort the whole crawl.
                    if (_logger.IsDebug) _logger.Debug($"Skipping malformed ENR tree record from DNS query '{query}': {e.Message}");
                    continue;
                }

                // EIP-1459: the root binds the whole tree through its subtree hashes, so it must carry the signer's signature.
                if (query.Length == 0 && searchContext.SignerPublicKey is not null &&
                    !(treeNode is EnrTreeRoot root && root.IsSignedBy(searchContext.SignerPublicKey)))
                {
                    if (_logger.IsWarn) _logger.Warn($"Rejecting ENR tree root '{node}': it is not an enrtree-root signed by the configured tree signer.");
                    continue;
                }

                foreach (string link in treeNode.Links)
                {
                    DnsClient linkedTreeLookup = new(link);
                    await foreach (string nodeRecordText in SearchTree(linkedTreeLookup, searchContext, cancellationToken).WithCancellation(cancellationToken))
                    {
                        yield return nodeRecordText;
                    }
                }

                foreach (string nodeRecordText in treeNode.Records)
                {
                    yield return nodeRecordText;
                }

                foreach (string nodeRef in treeNode.Refs)
                {
                    searchContext.RefsToVisit.Enqueue(nodeRef);
                }
            }
        }
    }

    private class SearchContext
    {
        public SearchContext(string startRef, byte[]? signerPublicKey)
        {
            RefsToVisit.Enqueue(startRef);
            SignerPublicKey = signerPublicKey;
        }

        public byte[]? SignerPublicKey { get; }

        public HashSet<string> VisitedRefs { get; } = [];

        public Queue<string> RefsToVisit { get; } = new();
    }
}

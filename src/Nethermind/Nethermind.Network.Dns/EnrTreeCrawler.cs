// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.Network.Dns;

public class EnrTreeCrawler
{
    private readonly ILogger _logger;

    public EnrTreeCrawler(ILogger logger)
    {
        _logger = logger;
    }
    public IAsyncEnumerable<string> SearchTree(string domain)
    {
        if (domain.ToLower().StartsWith("enrtree://"))
        {
            domain = domain[10..];
            // Note: we have no verification of a DNS list signer!
            // Following EIP-1459 "public key must be known to the client in order to verify the list"
            // Thus there shall be a list of public keys that a client allows and we shall check against it
            string[] pubkey_and_url = domain.Split("@");
            if (pubkey_and_url.Length > 1)
            {
                domain = pubkey_and_url[1];
            }
            else
            {
                _logger.Warn("No 32bit encoded public key of enr tree signer");
            }
        }
        DnsClient client = new(domain);
        SearchContext searchContext = new(string.Empty);
        return SearchTree(client, searchContext);
    }

    private async IAsyncEnumerable<string> SearchTree(DnsClient client, SearchContext searchContext)
    {
        while (searchContext.RefsToVisit.Count > 0)
        {
            string reference = searchContext.RefsToVisit.Dequeue();
            await foreach (string nodeRecordText in SearchNode(client, reference, searchContext))
            {
                yield return nodeRecordText;
            }
        }
    }

    private async IAsyncEnumerable<string> SearchNode(IDnsClient client, string query, SearchContext searchContext)
    {
        if (!searchContext.VisitedRefs.Contains(query))
        {
            searchContext.VisitedRefs.Add(query);
            IEnumerable<string> lookupResult = await client.Lookup(query);
            foreach (string node in lookupResult)
            {
                EnrTreeNode treeNode = EnrTreeParser.ParseNode(node);
                foreach (string link in treeNode.Links)
                {
                    DnsClient linkedTreeLookup = new(link);
                    await foreach (string nodeRecordText in SearchTree(linkedTreeLookup, searchContext))
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
        public SearchContext(string startRef)
        {
            RefsToVisit.Enqueue(startRef);
        }

        public HashSet<string> VisitedRefs { get; } = new();

        public Queue<string> RefsToVisit { get; } = new();
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

namespace Nethermind.Network.Dns;

public class EnrTreeCrawler
{
    public IAsyncEnumerable<string> SearchTree(string domain)
    {
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

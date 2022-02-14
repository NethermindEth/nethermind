//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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

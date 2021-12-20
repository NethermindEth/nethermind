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

using Nethermind.Stats.Model;

namespace Nethermind.Network.Dns;

public class EnrDiscovery : INodeSource
{
    private class SearchContext
    {
        public SearchContext(string startRef)
        {
            RefsToVisit.Enqueue(startRef);
        }

        public List<string> DiscoveredNodes { get; } = new();

        public HashSet<string> VisitedRefs { get; } = new();

        public Queue<string> RefsToVisit { get; } = new();
    }

    public async Task SearchTree(string domain)
    {
        DnsClient client = new(domain);
        SearchContext searchContext = new("");
        await SearchTree(client, searchContext);
    }

    private async Task SearchTree(DnsClient client, SearchContext searchContext)
    {
        while (searchContext.RefsToVisit.Any())
        {
            string @ref = searchContext.RefsToVisit.Dequeue();
            await SearchNode(client, @ref, searchContext);
        }
    }

    private async Task SearchNode(IDnsClient client, string query, SearchContext searchContext)
    {
        if (searchContext.VisitedRefs.Contains(query))
        {
            return;
        }

        searchContext.VisitedRefs.Add(query);

        string[][] lookupResult = await client.Lookup(query);
        foreach (string[] strings in lookupResult)
        {
            string s = string.Join("", strings);
            EnrTreeNode treeNode = EnrTreeParser.ParseNode(s);
            foreach (string link in treeNode.Links)
            {
                DnsClient linkedTreeLookup = new(link);
                await SearchTree(linkedTreeLookup, searchContext);
            }

            foreach (string nodeRecord in treeNode.Records)
            {
                searchContext.DiscoveredNodes.Add(nodeRecord);
                // node record to node
                // Node node = new Node()
                // NodeAdded?.Invoke(this, new NodeEventArgs());
            }

            foreach (string nodeRef in treeNode.Refs)
            {
                searchContext.RefsToVisit.Enqueue(nodeRef);
            }
        }
    }

    public List<Node> LoadInitialList()
    {
        return new List<Node>();
    }

    public event EventHandler<NodeEventArgs>? NodeAdded;

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}

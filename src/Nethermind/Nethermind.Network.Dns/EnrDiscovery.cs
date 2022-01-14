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

using System.Buffers.Text;
using System.Net;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Dns;

public class EnrDiscovery : INodeSource
{
    private readonly NodeRecordSigner _nodeRecordSigner;
    private readonly ILogger _logger;

    private class SearchContext
    {
        public SearchContext(string startRef)
        {
            RefsToVisit.Enqueue(startRef);
        }

        public HashSet<string> VisitedRefs { get; } = new();

        public Queue<string> RefsToVisit { get; } = new();
    }

    public EnrDiscovery(ILogManager logManager)
    {
        // I do not use the key here -> API is broken - no sense to use the node signer here
        PrivateKeyGenerator generator = new();
        _nodeRecordSigner = new NodeRecordSigner(new Ecdsa(), generator.Generate());
        _logger = logManager.GetClassLogger();
    }

    public async Task SearchTree(string domain)
    {
        DnsClient client = new(domain);
        SearchContext searchContext = new(string.Empty);
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
        if (!searchContext.VisitedRefs.Contains(query))
        {
            searchContext.VisitedRefs.Add(query);

            string[][] lookupResult = await client.Lookup(query);
            foreach (string[] strings in lookupResult)
            {
                string s = string.Join(string.Empty, strings);
                EnrTreeNode treeNode = EnrTreeParser.ParseNode(s);
                foreach (string link in treeNode.Links)
                {
                    DnsClient linkedTreeLookup = new(link);
                    await SearchTree(linkedTreeLookup, searchContext);
                }

                foreach (string nodeRecordText in treeNode.Records)
                {
                    try
                    {
                        int minLength = nodeRecordText.Length % 4;
                        const int prefixLength = 4;
                        int length = Math.Max(0, nodeRecordText.Length - prefixLength);
                        StringBuilder builder = new StringBuilder(nodeRecordText, prefixLength, length, Math.Max(length, minLength))
                            .Replace('-', '+')
                            .Replace('_', '/');

                        // PadRight(minLength)
                        // This doesn't make sense and won't change length of the string
                        builder.AppendJoin(string.Empty, Enumerable.Repeat(' ', Math.Max(0, minLength - builder.Length)));
                        RlpStream rlpStream = new(Convert.FromBase64String(builder.ToString()));
                        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(rlpStream);

                        CompressedPublicKey? compressedPublicKey =
                            nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1);
                        Node node = new(
                            compressedPublicKey!.Decompress(),
                            nodeRecord.GetObj<IPAddress>(EnrContentKey.Ip)!.ToString(),
                            nodeRecord.GetValue<int>(EnrContentKey.Tcp)!.Value);

                        // here could add network info to the node
                        NodeAdded?.Invoke(this, new NodeEventArgs(node));
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error("failed to parse enr record", e);
                    }
                }

                foreach (string nodeRef in treeNode.Refs)
                {
                    searchContext.RefsToVisit.Enqueue(nodeRef);
                }
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

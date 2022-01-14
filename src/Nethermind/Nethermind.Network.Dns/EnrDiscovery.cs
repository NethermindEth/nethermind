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
using DotNetty.Buffers;
using DotNetty.Codecs.Base64;
using DotNetty.Common.Utilities;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P;
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
        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer();
        try
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
                            Node node = ParseRecord(nodeRecordText, buffer);
                            // Node node = ParseRecordOld(nodeRecordText);
                            
                            // here could add network info to the node
                            NodeAdded?.Invoke(this, new NodeEventArgs(node));
                        }
                        catch (Exception e)
                        {
                            if (_logger.IsDebug) _logger.Error($"failed to parse enr record {nodeRecordText}", e);
                        }
                    }

                    foreach (string nodeRef in treeNode.Refs)
                    {
                        searchContext.RefsToVisit.Enqueue(nodeRef);
                    }
                }
            }
        }
        finally
        {
            buffer.Release();
        }
    }

    private Node ParseRecordOld(string nodeRecordText)
    {
        string broken = nodeRecordText[4..].Replace("-", "+").Replace("_", "/");
        broken = broken.PadRight(nodeRecordText.Length % 4);
        RlpStream rlpStream = new(Convert.FromBase64String(broken));
        return DeserializeNode(rlpStream);
    }

    private Node ParseRecord(string nodeRecordText, IByteBuffer buffer)
    {
        buffer.Clear();
        buffer.WriteCharSequence(new StringCharSequence(nodeRecordText, 4, nodeRecordText.Length - 4), Encoding.UTF8);
        IByteBuffer base64Buffer = DotNetty.Codecs.Base64.Base64.Decode(buffer, Base64Dialect.URL_SAFE);
        try
        {
            NettyRlpStream rlpStream = new(base64Buffer);
            return DeserializeNode(rlpStream);
        }
        finally
        {
            base64Buffer.Release();
        }
    }

    private Node DeserializeNode(RlpStream rlpStream)
    {
        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(rlpStream);
        CompressedPublicKey compressedPublicKey =
            nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1)!;
        Node node = new(
            compressedPublicKey!.Decompress(),
            nodeRecord.GetObj<IPAddress>(EnrContentKey.Ip)!.ToString(),
            nodeRecord.GetValue<int>(EnrContentKey.Tcp)!.Value);

        return node;
    }

    public List<Node> LoadInitialList()
    {
        return new List<Node>();
    }

    public event EventHandler<NodeEventArgs>? NodeAdded;

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}

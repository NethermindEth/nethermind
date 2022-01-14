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
    private readonly INodeRecordSigner _nodeRecordSigner;
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

    public EnrDiscovery(INodeRecordSigner nodeRecordSigner, ILogManager logManager)
    {
        _nodeRecordSigner = nodeRecordSigner;
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
                            Node? node = ParseRecord(nodeRecordText, buffer);
                            
                            if (node is not null)
                            {
                                // here could add network info to the node
                                NodeAdded?.Invoke(this, new NodeEventArgs(node));
                            }
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

    private Node? ParseRecord(string nodeRecordText, IByteBuffer buffer)
    {
        void AddPadding(IByteBuffer byteBuffer, ICharSequence base64)
        {
            if (base64[^1] != '=')
            {
                int mod3 = base64.Count % 4;
                for (int i = 0; i < mod3; i++)
                {
                    byteBuffer.WriteString("=", Encoding.UTF8);
                }
            }
        }

        buffer.Clear();
        StringCharSequence base64Sequence = new(nodeRecordText, 4, nodeRecordText.Length - 4);
        buffer.WriteCharSequence(base64Sequence, Encoding.UTF8);
        AddPadding(buffer, base64Sequence);
        
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

    private Node? DeserializeNode(RlpStream rlpStream)
    {
        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(rlpStream);
        CompressedPublicKey? compressedPublicKey = nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1);
        IPAddress? ipAddress = nodeRecord.GetObj<IPAddress>(EnrContentKey.Ip);
        int? port = nodeRecord.GetValue<int>(EnrContentKey.Tcp) ?? nodeRecord.GetValue<int>(EnrContentKey.Udp);
        if (compressedPublicKey is not null && ipAddress is not null && port is not null)
        {
            return new(compressedPublicKey.Decompress(), ipAddress.ToString(), port.Value);
        }
        else
        {
            return null;
        }
    }

    public List<Node> LoadInitialList()
    {
        return new List<Node>();
    }

    public event EventHandler<NodeEventArgs>? NodeAdded;

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}

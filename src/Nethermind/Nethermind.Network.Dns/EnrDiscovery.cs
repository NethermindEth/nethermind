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
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Dns;

public class EnrDiscovery : INodeSource
{
    private readonly IEnrRecordParser _parser;
    private readonly ILogger _logger;
    private readonly EnrTreeCrawler _crawler;

    public EnrDiscovery(IEnrRecordParser parser, ILogManager logManager)
    {
        _parser = parser;
        _logger = logManager.GetClassLogger();
        _crawler = new EnrTreeCrawler();
    }

    public async Task SearchTree(string domain)
    {
        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer();
        try
        {
            await foreach (string nodeRecordText in _crawler.SearchTree(domain))
            {
                try
                {
                    NodeRecord nodeRecord = _parser.ParseRecord(nodeRecordText, buffer);
                    Node? node = CreateNode(nodeRecord);
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
        }
        finally
        {
            buffer.Release();
        }
    }

    private Node? CreateNode(NodeRecord nodeRecord)
    {
        CompressedPublicKey? compressedPublicKey = nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1);
        IPAddress? ipAddress = nodeRecord.GetObj<IPAddress>(EnrContentKey.Ip);
        int? port = nodeRecord.GetValue<int>(EnrContentKey.Tcp) ?? nodeRecord.GetValue<int>(EnrContentKey.Udp);
        return compressedPublicKey is not null && ipAddress is not null && port is not null 
            ? new(compressedPublicKey.Decompress(), ipAddress.ToString(), port.Value) 
            : null;
    }

    public List<Node> LoadInitialList() => new();

    public event EventHandler<NodeEventArgs>? NodeAdded;

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}

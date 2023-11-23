// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DnsClient;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
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
        _crawler = new EnrTreeCrawler(_logger);
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
        catch (DnsResponseException dnsException)
        {
            if (_logger.IsWarn) _logger.Warn($"Searching the tree of \"{domain}\" had an error: {dnsException.DnsError}");
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

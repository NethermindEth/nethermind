// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Runtime.CompilerServices;

using DnsClient;

using DotNetty.Buffers;

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Dns;

public class EnrDiscovery : INodeSource
{
    private readonly IEnrRecordParser _parser;
    private readonly ILogger _logger;
    private readonly EnrTreeCrawler _crawler;
    private readonly string _domain;

    public EnrDiscovery(IEnrRecordParser parser, INetworkConfig networkConfig, ILogManager logManager)
    {
        _parser = parser;
        _logger = logManager.GetClassLogger();
        _crawler = new EnrTreeCrawler(_logger);
        _domain = networkConfig.DiscoveryDns!;
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_domain)) yield break;

        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer();
        await using ConfiguredCancelableAsyncEnumerable<string>.Enumerator enumerator = _crawler.SearchTree(_domain)
            .WithCancellation(cancellationToken)
            .GetAsyncEnumerator();

        try
        {
            // Need to loop manually because of te exception handling
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasNext = false;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (DnsResponseException dnsException)
                {
                    if (_logger.IsWarn) _logger.Warn($"Searching the tree of \"{_domain}\" had an error: {dnsException.DnsError}");
                    yield break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                string nodeRecordText = enumerator.Current;
                Node? node = null;
                try
                {
                    NodeRecord nodeRecord = _parser.ParseRecord(nodeRecordText, buffer);
                    node = CreateNode(nodeRecord);
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR failed to parse enr record {nodeRecordText}", e);
                }

                if (node is not null)
                {
                    // here could add network info to the node
                    yield return node;
                }
            }
        }
        finally
        {
            buffer.Release();
        }
    }

    private static Node? CreateNode(NodeRecord nodeRecord)
    {
        CompressedPublicKey? compressedPublicKey = nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1);
        IPAddress? ipAddress = nodeRecord.GetObj<IPAddress>(EnrContentKey.Ip);
        int? port = nodeRecord.GetValue<int>(EnrContentKey.Tcp) ?? nodeRecord.GetValue<int>(EnrContentKey.Udp);
        return compressedPublicKey is not null && ipAddress is not null && port is not null
            ? new(compressedPublicKey.Decompress(), ipAddress.ToString(), port.Value)
            : null;
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}

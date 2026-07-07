// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using DnsClient;
using DotNetty.Buffers;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

[assembly: InternalsVisibleTo("Nethermind.Network.Dns.Test")]

namespace Nethermind.Network.Dns;

public class EnrDiscovery : INodeSource
{
    private readonly IEnrRecordParser _parser;
    private readonly IForkInfo _forkInfo;
    private readonly ILogger _logger;
    private readonly EnrTreeCrawler _crawler;
    private readonly string _domain;

    public EnrDiscovery(IEnrRecordParser parser, INetworkConfig networkConfig, IForkInfo forkInfo, ILogManager logManager)
    {
        _parser = parser;
        _forkInfo = forkInfo;
        _logger = logManager.GetClassLogger<EnrDiscovery>();
        _crawler = new EnrTreeCrawler(_logger);
        _domain = networkConfig.DiscoveryDns!;
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_domain)) yield break;

        IByteBuffer buffer = NethermindBuffers.Default.Buffer();
        await using ConfiguredCancelableAsyncEnumerable<string>.Enumerator enumerator = _crawler.SearchTree(_domain, cancellationToken)
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
                    if (_forkInfo.IsNodeRecordForkCompatible(nodeRecord))
                    {
                        TryCreateNode(nodeRecord, out node);
                    }
                    else if (_logger.IsTrace)
                    {
                        _logger.Trace($"Skipping DNS discovered ENR {nodeRecordText} with incompatible fork ID.");
                    }
                }
                catch (Exception e)
                {
                    _logger.DebugError($"failed to parse enr record {nodeRecordText}", e);
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

    internal static bool TryCreateNode(NodeRecord nodeRecord, out Node? node) =>
        Node.TryFromEnr(nodeRecord, out node);

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}

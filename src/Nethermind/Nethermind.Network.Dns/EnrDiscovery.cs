// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
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
    private readonly Lock _crawlLock = new();
    private Crawl? _crawl;
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    private static readonly TimeSpan TruncatedCrawlCooldown = TimeSpan.FromMinutes(10);

    public EnrDiscovery(IEnrRecordParser parser, INetworkConfig networkConfig, IForkInfo forkInfo, ILogManager logManager)
        : this(parser, networkConfig, forkInfo, logManager, null)
    {
    }

    internal EnrDiscovery(IEnrRecordParser parser, INetworkConfig networkConfig, IForkInfo forkInfo, ILogManager logManager, EnrTreeCrawler? crawler)
    {
        _parser = parser;
        _forkInfo = forkInfo;
        _logger = logManager.GetClassLogger<EnrDiscovery>();
        _crawler = crawler ?? new EnrTreeCrawler(_logger);
        _domain = networkConfig.DiscoveryDns!;
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_domain)) yield break;
        Crawl crawl;
        bool start;
        lock (_crawlLock)
        {
            start = _crawl is null;
            if (start) _crawl = new Crawl();
            crawl = _crawl!;
            crawl.Consumers++;
        }
        // Consumers observe producer completion and errors through the shared replay state.
        if (start) _ = Fill(crawl);
        try
        {
            int index = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Node? next = null;
                Task? changed = null;
                ExceptionDispatchInfo? error;
                lock (_crawlLock)
                {
                    error = crawl.Error;
                    if (index < crawl.Nodes.Count) next = crawl.Nodes[index++];
                    else if (!crawl.Completed) changed = (crawl.Changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }
                if (next is not null) yield return next;
                else if (changed is not null) await changed.WaitAsync(cancellationToken);
                else
                {
                    error?.Throw();
                    yield break;
                }
            }
        }
        finally
        {
            Task cancellation = Task.CompletedTask;
            lock (_crawlLock)
            {
                if (--crawl.Consumers == 0 && !crawl.Completed)
                {
                    if (ReferenceEquals(_crawl, crawl)) _crawl = null;
                    cancellation = crawl.CancellationCompletion = crawl.Cancellation.CancelAsync();
                }
            }
            await cancellation;
        }
    }

    private async Task Fill(Crawl crawl)
    {
        using CancellationTokenSource cancellationSource = crawl.Cancellation;
        try
        {
            crawl.State = _crawler.CreateCrawl(_domain);
            do
            {
                await foreach (Node node in DiscoverNodesCore(crawl, crawl.Cancellation.Token))
                {
                    lock (_crawlLock)
                    {
                        crawl.Nodes.Add(node);
                        crawl.Changed?.TrySetResult();
                        crawl.Changed = null;
                    }
                }
                if (crawl.State?.CanResume != true) break;
                if (crawl.State.Paused) await Task.Delay(TruncatedCrawlCooldown, TimeProvider, crawl.Cancellation.Token);
                else if (!crawl.State.Restart) break;
            }
            while (true);
        }
        catch (OperationCanceledException) when (crawl.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_crawlLock)
            {
                crawl.Error = ExceptionDispatchInfo.Capture(ex);
                if (ReferenceEquals(_crawl, crawl)) _crawl = null;
            }
        }
        finally
        {
            Task cancellation;
            lock (_crawlLock)
            {
                crawl.State = null;
                crawl.Completed = true;
                crawl.Changed?.TrySetResult();
                cancellation = crawl.CancellationCompletion;
            }
            await cancellation;
        }
    }

    private sealed class Crawl
    {
        public EnrTreeCrawler.CrawlState? State;
        public readonly List<Node> Nodes = [];
        public readonly CancellationTokenSource Cancellation = new();
        public TaskCompletionSource? Changed;
        public int Consumers;
        public bool Completed;
        public Task CancellationCompletion = Task.CompletedTask;
        public ExceptionDispatchInfo? Error;
    }

    private async IAsyncEnumerable<Node> DiscoverNodesCore(Crawl crawl, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (crawl.State is null) yield break;

        await using ConfiguredCancelableAsyncEnumerable<string>.Enumerator enumerator = _crawler.SearchTree(crawl.State, cancellationToken)
            .WithCancellation(cancellationToken)
            .GetAsyncEnumerator();

        // Need to loop manually because of te exception handling
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasNext = false;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                crawl.State.FailEscaping();
                if (_logger.IsWarn) _logger.Warn($"Searching DNS tree '{_domain}' failed: {ex.Message}");
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
                NodeRecord nodeRecord = _parser.ParseRecord(nodeRecordText);
                if (_forkInfo.IsNodeRecordForkCompatible(nodeRecord))
                {
                    node = CreateVerifiedNode(nodeRecord);
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

    internal static bool TryCreateNode(NodeRecord nodeRecord, [NotNullWhen(true)] out Node? node) =>
        Node.TryFromEnr(nodeRecord, out node);

    internal static Node? CreateVerifiedNode(NodeRecord nodeRecord)
    {
        if (!TryCreateNode(nodeRecord, out Node? node) || !node.SetVerifiedEnr(nodeRecord))
        {
            return null;
        }

        return node;
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}

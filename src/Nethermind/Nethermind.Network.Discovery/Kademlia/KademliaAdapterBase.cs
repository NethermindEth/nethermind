// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

public abstract class KademliaAdapterBase(
    string protocolName,
    IIPResolver ipResolver,
    ILogger logger,
    NetworkListenerState listenerState)
{
    protected IIPResolver.NethermindIp ResolvedIp { get; } = ipResolver.Resolve().GetAwaiter().GetResult();
    protected NetworkListenerState ListenerState { get; } = listenerState;

    protected ILogger Logger { get; } = logger;
    protected IPAddress LocalIp => ListenerState.DiscoveryAddress ?? ListenerState.PreferredAddress;

    protected abstract ValueTask<NodeRecord?> RequestRemoteRecord(
        Node node,
        ulong requestedSequence,
        CancellationToken token);

    protected abstract void AddOrRefreshRemoteNode(Node node);

    protected virtual bool IsEnrValidForNode(Node node, NodeRecord record) => true;

    internal static bool HasExpectedNodeId(NodeRecord record, ValueHash256 expectedNodeId)
        => record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress().Hash == expectedNodeId;

    protected virtual bool TryCreateNodeFromEnr(Node currentNode, NodeRecord record, [NotNullWhen(true)] out Node? refreshedNode)
        => CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(record, LocalIp, currentNode.DiscoveryAddress, out refreshedNode);

    protected async Task RefreshRemoteRecordIfNewer(Node node, ulong advertisedSequence, CancellationToken token)
    {
        if (advertisedSequence == 0)
        {
            return;
        }

        if (node.HighestObservedEnrSequence >= advertisedSequence)
        {
            return;
        }

        if (!node.TryRequestEnrSequence(advertisedSequence))
        {
            return;
        }

        try
        {
            while (true)
            {
                ulong requestedSequence = node.RequestingEnrSequence;
                if (requestedSequence == 0)
                {
                    return;
                }

                ulong observedSequence = node.HighestObservedEnrSequence;
                if (observedSequence >= requestedSequence)
                {
                    if (node.TryClearEnrRequest(observedSequence))
                    {
                        return;
                    }

                    continue;
                }

                NodeRecord? record = await RequestRemoteRecord(node, requestedSequence, token);
                if (record is null)
                {
                    if (Logger.IsTrace) Logger.Trace($"No usable {protocolName} ENR available from {node} after advertised sequence {requestedSequence}.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                if (record.EnrSequence < node.RequestingEnrSequence)
                {
                    if (Logger.IsTrace) Logger.Trace($"Ignoring stale {protocolName} ENR from {node}; requested sequence {node.RequestingEnrSequence}, received {record.EnrSequence}.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                if (!IsEnrValidForNode(node, record))
                {
                    // Do not observe a sequence from a record that is not authenticated for this node;
                    // doing so could suppress a later valid refresh.
                    if (Logger.IsTrace) Logger.Trace($"Ignoring {protocolName} ENR from {node}; record is not valid for the node.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                if (!TryCreateNodeFromEnr(node, record, out Node? refreshedNode))
                {
                    if (Logger.IsTrace) Logger.Trace($"Retaining the reachable {protocolName} endpoint for {node}; the newer ENR has no usable discovery endpoint reachable from this listener.");
                    if (node.ObserveEnrSequence(record.EnrSequence))
                    {
                        return;
                    }

                    continue;
                }

                refreshedNode.MergeEnrStateFrom(node);
                if (!refreshedNode.SetVerifiedEnr(record))
                {
                    continue;
                }

                ulong requestingSequence = refreshedNode.RequestingEnrSequence;
                node = refreshedNode;
                AddOrRefreshRemoteNode(refreshedNode);
                if (requestingSequence == 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            node.TryClearEnrRequest(node.RequestingEnrSequence);
            if (Logger.IsTrace) Logger.Trace($"Failed to refresh {protocolName} ENR for {node}: {e}");
        }
    }
}

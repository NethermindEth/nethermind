// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

public abstract class KademliaAdapterBase(
    string protocolName,
    ILogger logger)
{
    protected ILogger Logger { get; } = logger;

    protected abstract ValueTask<NodeRecord?> RequestRemoteRecord(
        Node node,
        ulong requestedSequence,
        CancellationToken token);

    protected abstract void AddOrRefreshRemoteNode(Node node);

    protected virtual bool IsEnrValidForNode(Node node, NodeRecord record) => true;

    protected virtual bool CanCacheRemoteRecordWithoutDiscoveryEndpoint(Node node, NodeRecord record) => false;

    protected async Task RefreshRemoteRecordIfNewer(Node node, ulong advertisedSequence, CancellationToken token)
    {
        if (advertisedSequence == 0)
        {
            return;
        }

        if (node.Enr is { Signature: not null } currentRecord && currentRecord.EnrSequence >= advertisedSequence)
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

                if (node.Enr is { Signature: not null } signedRecord && signedRecord.EnrSequence >= requestedSequence)
                {
                    node.TryClearEnrRequest(signedRecord.EnrSequence);
                    return;
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
                    if (Logger.IsTrace) Logger.Trace($"Ignoring {protocolName} ENR from {node}; record is not valid for the node.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                if (!Node.TryFromDiscoveryEnr(record, out Node? refreshedNode) &&
                    !CanCacheRemoteRecordWithoutDiscoveryEndpoint(node, record))
                {
                    if (Logger.IsTrace) Logger.Trace($"Ignoring {protocolName} ENR from {node}; record has no usable endpoint.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                node.Enr = record;
                ulong requestingSequence = node.RequestingEnrSequence;
                if (refreshedNode is not null)
                {
                    if (requestingSequence > record.EnrSequence)
                    {
                        refreshedNode.TryRequestEnrSequence(requestingSequence);
                    }

                    node = refreshedNode;
                    AddOrRefreshRemoteNode(refreshedNode);
                }

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

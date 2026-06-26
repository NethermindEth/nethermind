// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Shared ENR freshness handling for discovery Kademlia adapters.
/// </summary>
public abstract class KademliaAdapterBase(
    string protocolName,
    string remoteRecordResponseName)
{
    /// <summary>
    /// Logger using the concrete adapter type as the source context.
    /// </summary>
    protected abstract ILogger Logger { get; }

    /// <summary>
    /// Requests the remote node's latest ENR according to the discovery protocol.
    /// </summary>
    /// <param name="node">Node to query.</param>
    /// <param name="requestedSequence">Minimum ENR sequence currently being requested.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The usable remote record, or <see langword="null"/> when the request did not produce one.</returns>
    protected abstract ValueTask<NodeRecord?> RequestRemoteRecord(
        Node node,
        ulong requestedSequence,
        CancellationToken token);

    /// <summary>
    /// Adds the refreshed discovery node to the protocol-specific routing table.
    /// </summary>
    /// <param name="node">Refreshed node parsed from the remote ENR.</param>
    protected abstract void AddOrRefreshRemoteNode(Node node);

    /// <summary>
    /// Checks protocol-specific record ownership or acceptability rules.
    /// </summary>
    /// <param name="node">Node whose advertised sequence triggered the refresh.</param>
    /// <param name="record">Record returned by the peer.</param>
    /// <returns><see langword="true"/> when the record can be applied to the node.</returns>
    protected virtual bool IsAcceptableRemoteRecord(Node node, NodeRecord record) => true;

    /// <summary>
    /// Text logged when <see cref="IsAcceptableRemoteRecord"/> rejects a record.
    /// </summary>
    protected virtual string UnexpectedRemoteRecordReason => "record is not valid for the node";

    /// <summary>
    /// Fetches and stores a newer remote ENR when the peer advertises a higher sequence.
    /// </summary>
    /// <param name="node">Node that advertised the sequence.</param>
    /// <param name="advertisedSequence">Advertised ENR sequence.</param>
    /// <param name="token">Cancellation token.</param>
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
                    if (Logger.IsTrace) Logger.Trace($"No usable {protocolName} {remoteRecordResponseName} available from {node} after advertised sequence {requestedSequence}.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                if (record.EnrSequence < node.RequestingEnrSequence)
                {
                    if (Logger.IsTrace) Logger.Trace($"Ignoring stale {protocolName} {remoteRecordResponseName} from {node}; requested sequence {node.RequestingEnrSequence}, received {record.EnrSequence}.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                if (!IsAcceptableRemoteRecord(node, record))
                {
                    if (Logger.IsTrace) Logger.Trace($"Ignoring {protocolName} {remoteRecordResponseName} from {node}; {UnexpectedRemoteRecordReason}.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                if (!Node.TryFromDiscoveryEnr(record, out Node? refreshedNode))
                {
                    if (Logger.IsTrace) Logger.Trace($"Ignoring {protocolName} {remoteRecordResponseName} from {node}; record has no usable discovery endpoint.");
                    if (node.TryClearEnrRequest(requestedSequence))
                    {
                        return;
                    }

                    continue;
                }

                node.Enr = record;
                ulong requestingSequence = node.RequestingEnrSequence;
                if (requestingSequence > record.EnrSequence)
                {
                    refreshedNode.TryRequestEnrSequence(requestingSequence);
                }

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

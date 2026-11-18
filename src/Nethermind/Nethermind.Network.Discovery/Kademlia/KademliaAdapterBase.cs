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
    ILogger logger,
    NetworkListenerState listenerState)
{
    protected NetworkListenerState ListenerState { get; } = listenerState;

    protected ILogger Logger { get; } = logger;
    protected IPAddress LocalIp => ListenerState.DiscoveryAddress ?? ListenerState.PreferredAddress;

    protected abstract ValueTask<NodeRecord?> RequestRemoteRecord(
        Node node,
        ulong requestedSequence,
        CancellationToken token);

    protected abstract void AddOrRefreshRemoteNode(Node node);

    protected virtual ValueTask<bool> VerifyRemoteEndpoint(Node node, CancellationToken token) => ValueTask.FromResult(true);

    protected virtual bool IsEnrValidForNode(Node node, NodeRecord record) => true;

    /// <summary>Reserves optional capacity for a remote record refresh.</summary>
    /// <param name="node">The node being refreshed.</param>
    /// <param name="lease">Null on refusal or unmetered admission; otherwise a lease disposed by the base method.</param>
    /// <returns>False forbids dispatch; true permits refresh with or without a lease.</returns>
    protected virtual bool TryReserveRemoteRecordRefresh(Node node, out IDisposable? lease)
    {
        lease = null;
        return true;
    }

    internal static bool HasExpectedNodeId(NodeRecord record, ValueHash256 expectedNodeId)
        => record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress().Hash == expectedNodeId;

    internal static bool HasSameAddressBytes(IPAddress left, IPAddress right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        return left.TryWriteBytes(leftBytes, out int leftLength) &&
               right.TryWriteBytes(rightBytes, out int rightLength) &&
               leftBytes[..leftLength].SequenceEqual(rightBytes[..rightLength]);
    }

    protected virtual bool TryCreateNodeFromEnr(Node currentNode, NodeRecord record, [NotNullWhen(true)] out Node? refreshedNode)
        => CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(record, LocalIp, currentNode.DiscoveryAddress, out refreshedNode,
            currentNode.IsStatic || currentNode.IsTrusted || currentNode.IsBootnode ? PeerAddressPermissions.All : PeerAddressPermissions.None);

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

        if (!node.TryAcquireEnrRefresh(advertisedSequence, out IDisposable? owner))
        {
            return;
        }

        using (owner)
            try
            {
                if (!TryReserveRemoteRecordRefresh(node, out IDisposable? reservation)) return;
                using IDisposable? lease = reservation;
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

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

                    if (record.EnrSequence < requestedSequence)
                    {
                        if (Logger.IsTrace) Logger.Trace($"Ignoring stale {protocolName} ENR from {node}; requested sequence {requestedSequence}, received {record.EnrSequence}.");
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

                    refreshedNode.IsStatic = node.IsStatic;
                    refreshedNode.IsTrusted = node.IsTrusted;
                    refreshedNode.IsBootnode = node.IsBootnode;
                    if (!await VerifyRemoteEndpoint(refreshedNode, token))
                    {
                        node.TryClearEnrRequest(requestedSequence);
                        return;
                    }
                    token.ThrowIfCancellationRequested();
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
                if (Logger.IsTrace) Logger.Trace($"Failed to refresh {protocolName} ENR for {node}: {e}");
            }
    }
}

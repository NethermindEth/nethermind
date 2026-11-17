// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Kademlia;

/// <summary>
/// Applies the discovery-node admission decision before a node is published as a peer candidate.
/// </summary>
/// <remarks>
/// The filter decides which nodes enter discovery's routing table and peer-candidate stream; it never
/// decides whether a discovery message is answered. discv4 requires a Pong for every Ping, and that Pong
/// is what lets the peer prove our endpoint, so suppressing traffic for a filtered sender would break the
/// handshake in both directions. It would also be wrong for legitimate peers that share an address, such as
/// several nodes on one host or behind one NAT. Routing-table admission is applied by
/// <see cref="FilteringNodeHealthTracker"/> after a message is handled; peer candidates are bonded before
/// they reach this adapter, so admitting them here commits the address only after the endpoint proof.
/// </remarks>
internal sealed class AdmissionGatedKademliaAdapter(
    IKademliaAdapter inner,
    DiscoveryNodeAdmission admission,
    ILogManager logManager) : IKademliaAdapter
{
    private readonly IKademliaAdapter _inner = inner;
    private readonly DiscoveryNodeAdmission _admission = admission;
    private readonly ILogger _logger = logManager.GetClassLogger<AdmissionGatedKademliaAdapter>();

    public IMsgSender? MsgSender
    {
        get => _inner.MsgSender;
        set => _inner.MsgSender = value;
    }

    public Task OnIncomingMsg(DiscoveryMsg msg) => _inner.OnIncomingMsg(msg);

    public async IAsyncEnumerable<Node> ReadPeerCandidates(
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (Node node in _inner.ReadPeerCandidates(token).WithCancellation(token))
        {
            if (_admission.TryAdmit(node))
            {
                yield return node;
            }
            else
            {
                _inner.ReleasePeerCandidate(node);

                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipping filtered discovery peer candidate: {node:hide}.");
                }
            }
        }
    }

    void IKademliaAdapter.ReleasePeerCandidate(Node node) => _inner.ReleasePeerCandidate(node);

    public Task<bool> Ping(Node receiver, CancellationToken token) => _inner.Ping(receiver, token);

    public Task<Node[]?> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
        => _inner.FindNeighbours(receiver, target, token);

    public Task<EnrResponseMsg?> SendEnrRequest(Node receiver, CancellationToken token)
        => _inner.SendEnrRequest(receiver, token);

    public NodeSession GetSession(Node node) => _inner.GetSession(node);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

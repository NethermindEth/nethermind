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
/// Applies the discovery-node admission decision before the discv4 adapter handles a sender or publishes a
/// peer candidate.
/// </summary>
/// <remarks>
/// Inbound handling and peer-candidate publication use the authoritative
/// <see cref="DiscoveryNodeAdmission.TryAdmit"/> and <see cref="DiscoveryNodeAdmission.IsAdmitted"/>
/// decisions. Outbound contact uses the read-only <see cref="DiscoveryNodeAdmission.WouldAccept"/>, so a
/// probe does not reserve the address window: several candidates from a not-yet-recorded subnet can be
/// probed before the first reply is admitted. This is intentional; an unanswered probe must not consume
/// filter capacity.
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

    public Task OnIncomingMsg(DiscoveryMsg msg)
    {
        if (!_admission.Enabled)
        {
            return _inner.OnIncomingMsg(msg);
        }

        // Old DiscoveryManager validated the Ping address before creating a session; keep that order so a
        // malformed Ping neither creates a session nor consumes admission capacity.
        if (msg is PingMsg ping && !KademliaAdapter.ValidatePingAddress(ping, _logger))
        {
            return Task.CompletedTask;
        }

        // Preserve the adapter's existing logging/validation behavior for malformed messages.
        if (msg.FarPublicKey is null || msg.FarAddress is null)
        {
            return _inner.OnIncomingMsg(msg);
        }

        Node node = KademliaAdapter.CreateNode(msg);
        if (!_admission.TryAdmit(node))
        {
            if (_logger.IsTrace) _logger.Trace($"Discarding discovery message from filtered node: {node:hide}.");
            return Task.CompletedTask;
        }

        return _inner.OnIncomingMsg(msg);
    }

    public async IAsyncEnumerable<Node> ReadPeerCandidates(
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (Node node in _inner.ReadPeerCandidates(token).WithCancellation(token))
        {
            if (_admission.IsAdmitted(node))
            {
                yield return node;
            }
            else if (_logger.IsTrace)
            {
                _logger.Trace($"Skipping filtered discovery peer candidate: {node:hide}.");
            }
        }
    }

    public Task<bool> Ping(Node receiver, CancellationToken token)
        => _admission.WouldAccept(receiver)
            ? _inner.Ping(receiver, token)
            : Task.FromResult(false);

    public Task<Node[]?> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
        => _admission.WouldAccept(receiver)
            ? _inner.FindNeighbours(receiver, target, token)
            : Task.FromResult<Node[]?>(null);

    public Task<EnrResponseMsg?> SendEnrRequest(Node receiver, CancellationToken token)
        => _admission.WouldAccept(receiver)
            ? _inner.SendEnrRequest(receiver, token)
            : Task.FromResult<EnrResponseMsg?>(null);

    public NodeSession GetSession(Node node) => _inner.GetSession(node);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

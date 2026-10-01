// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>The pubsub router's synchronous validator for eth2 gossip messages (see <see cref="PubsubRouter.VerifyMessage"/>).</summary>
/// <remarks>
/// <para>
/// The pinned pubsub library calls the validator once per new message id, before its own signature check, its
/// message cache and forwarding. It raises topic events only for <see cref="MessageValidity.Accepted"/>, and keeps a
/// rejected or ignored id for the whole seen TTL without redelivering it. A message that passes every check this node
/// can run without beacon state is therefore consumed here, by <see cref="GossipRouter"/> raising its typed event, and
/// returned as <see cref="MessageValidity.Ignored"/>: its signature and state checks run later, so it must not be
/// forwarded. A synchronous <see cref="MessageValidity.Rejected"/> is returned only when every check the spec orders
/// before it has passed.
/// </para>
/// <para>
/// Data column sidecars go to <see cref="ColumnGossipRouter"/>. They are the only messages that can be
/// <see cref="MessageValidity.Accepted"/>: a Gloas sidecar, whose every check runs here without beacon state, and a
/// Fulu sidecar whose header is an imported block's or is signed by the proposer the head state's lookahead expects.
/// </para>
/// </remarks>
public sealed class GossipMessageValidator(GossipRouter gossip, ColumnGossipRouter columns, BeaconChainSpec spec, SlotClock slotClock)
{
    private const string UnhandledTopicLabel = "unhandled";

    private volatile DigestWindow? _window;

    /// <summary>Validates <paramref name="message"/> and consumes it when it passes.</summary>
    /// <returns>
    /// <see cref="MessageValidity.Accepted"/> only for a data column sidecar that passed every check; otherwise
    /// <see cref="MessageValidity.Rejected"/> or <see cref="MessageValidity.Ignored"/>, including for a consumed message.
    /// </returns>
    public MessageValidity Verify(Message message)
    {
        bool parsed = GossipTopics.TryParse(message.Topic, out byte[]? digest, out string? name);
        string label = parsed && IsHandledName(name!) ? name! : UnhandledTopicLabel;
        if (parsed && GossipTopics.TryParseDataColumnSidecarTopicName(name!, out ulong labelSubnet)
            && labelSubnet < Eip7594DasConstants.DataColumnSidecarSubnetCount)
        {
            label = GossipTopics.DataColumnSidecarTopicName(labelSubnet);
        }
        Metrics.BeaconChainGossipReceivedByTopic.Increment(new StringLabel(label));

        // p2p-interface.md "Topics and messages": StrictNoSign requires all four optional fields to be absent.
        if (message.HasFrom || message.HasSeqno || message.HasSignature || message.HasKey)
        {
            return Drop(label, GossipDropReason.SignedMessage, MessageValidity.Rejected);
        }

        // phase0 p2p: MUST reject messages with an unknown topic.
        if (!parsed || !GossipTopics.IsEth2TopicName(name!))
        {
            return Drop(label, GossipDropReason.UnknownTopic, MessageValidity.Rejected);
        }

        bool column = GossipTopics.TryParseDataColumnSidecarTopicName(name!, out ulong subnetId);
        if (column && subnetId >= Eip7594DasConstants.DataColumnSidecarSubnetCount)
        {
            return Drop(label, GossipDropReason.UnknownTopic, MessageValidity.Rejected);
        }

        if (!TryGetFork(digest!, out bool gloas) || (!column && !IsHandled(name!, gloas)))
        {
            return Drop(label, GossipDropReason.UnknownTopic, MessageValidity.Ignored);
        }

        if (column)
        {
            return columns.Handle(subnetId, gloas, message.Data.ToByteArray());
        }

        return gossip.Handle(name!, gloas, message.Data.ToByteArray());
    }

    private static bool IsHandledName(string name) =>
        Array.IndexOf(GossipTopics.SubscribedTopicNames, name) >= 0 || Array.IndexOf(GossipTopics.GloasTopicNames, name) >= 0;

    private static bool IsHandled(string name, bool gloas) =>
        Array.IndexOf(GossipTopics.SubscribedTopicNames, name) >= 0 || (gloas && Array.IndexOf(GossipTopics.GloasTopicNames, name) >= 0);

    /// <summary>Whether <paramref name="digest"/> is in effect at an epoch near the wall clock's, and if so whether that epoch is Gloas.</summary>
    private bool TryGetFork(byte[] digest, out bool gloas)
    {
        ulong epoch = slotClock.CurrentEpoch;
        DigestWindow? window = _window;
        if (window is null || window.Epoch != epoch)
        {
            _window = window = DigestWindow.Create(spec, epoch);
        }

        foreach ((byte[] candidate, bool candidateGloas) in window.Digests)
        {
            if (candidate.AsSpan().SequenceEqual(digest))
            {
                gloas = candidateGloas;
                return true;
            }
        }

        gloas = false;
        return false;
    }

    private MessageValidity Drop(string label, GossipDropReason reason, MessageValidity validity)
    {
        gossip.Drop(label, reason);
        return validity;
    }

    // Acceptance uses the subscription window, so a topic this node subscribes is never ignored as unknown.
    private sealed record DigestWindow(ulong Epoch, (byte[] Digest, bool Gloas)[] Digests)
    {
        public static DigestWindow Create(BeaconChainSpec spec, ulong epoch) => new(epoch, GossipTopics.DigestsAround(spec, epoch));
    }
}

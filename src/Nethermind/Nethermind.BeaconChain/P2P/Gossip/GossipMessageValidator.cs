// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>The validator of eth2 gossip messages, run by <see cref="DeferredGossipValidation"/> for the pubsub router.</summary>
/// <remarks>
/// <para>
/// The pubsub library validates a message once per new message id, after its own <c>StrictNoSign</c> check (which drops a
/// message carrying any of from, seqno, signature or key without caching its id) and before its message cache and forwarding.
/// It keeps a rejected or ignored id for the whole seen TTL without redelivering it. A message on a topic this node handles is
/// routed to <see cref="GossipRouter"/> or <see cref="ColumnGossipRouter"/>, which run every check they can without beacon state
/// and hand the message's <see cref="GossipVerdict"/> to the import pipeline when its signature and state checks run there; the
/// router forwards it only once that verdict is <see cref="MessageValidity.Accepted"/>. A <see cref="MessageValidity.Rejected"/>
/// is returned only when every check the spec orders before it has passed.
/// </para>
/// <para>
/// A data column sidecar that passes every check is <see cref="MessageValidity.Accepted"/> here: a Gloas sidecar, whose every check
/// runs without beacon state, and a Fulu sidecar whose header is an imported block's or is signed by the proposer the head state's
/// lookahead expects.
/// </para>
/// </remarks>
public sealed class GossipMessageValidator(GossipRouter gossip, ColumnGossipRouter columns, BeaconChainSpec spec, SlotClock slotClock)
{
    private const string UnhandledTopicLabel = "unhandled";

    private static readonly string[] ColumnTopicNames = CreateColumnTopicNames();
    private static readonly StringLabel[] ColumnTopicLabels = Array.ConvertAll(ColumnTopicNames, static name => new StringLabel(name));

    private volatile DigestWindow? _window;

    /// <summary>Whether a message on <paramref name="topic"/> is validated off the router's monitor: one on a topic this node handles under a digest in effect.</summary>
    /// <remarks>A message on any other topic is dropped by <see cref="Validate"/> without decoding, which may run under the monitor.</remarks>
    internal bool IsDeferred(string topic) => Route(topic, out _, out _, out _, out _) is null;
    /// <summary>Whether <paramref name="topic"/> is a <c>data_column_sidecar_{subnet_id}</c> topic.</summary>
    internal static bool IsColumn(string topic) => GossipTopics.TryParse(topic, out _, out string? name) && GossipTopics.TryParseDataColumnSidecarTopicName(name!, out _);

    /// <summary>Whether <paramref name="topic"/> carries aggregates or payload attestations, small votes that come in their hundreds each slot.</summary>
    internal static bool IsVote(string topic) =>
        GossipTopics.TryParse(topic, out _, out string? name) && name is GossipTopics.BeaconAggregateAndProof or GossipTopics.PayloadAttestationMessage;

    /// <summary>Validates <paramref name="message"/> and consumes it when it passes.</summary>
    /// <param name="verdict">The message's pending verdict, which a consumer takes over when the checks continue in the import pipeline.</param>
    /// <returns>
    /// The verdict of the checks that ran, which the caller gives unless <paramref name="verdict"/> was handed off:
    /// <see cref="MessageValidity.Accepted"/> only for a data column sidecar that passed every check; otherwise
    /// <see cref="MessageValidity.Rejected"/> or <see cref="MessageValidity.Ignored"/>, including for a consumed message.
    /// </returns>
    internal MessageValidity Validate(Message message, GossipVerdict verdict, PeerId? source = null)
    {
        bool parsed = GossipTopics.TryParse(message.Topic, out _, out string? name);
        string label = parsed && IsHandledName(name!) ? name! : UnhandledTopicLabel;
        StringLabel? columnLabel = null;
        if (parsed && GossipTopics.TryParseDataColumnSidecarTopicName(name!, out ulong labelSubnet)
            && labelSubnet < Eip7594DasConstants.DataColumnSidecarSubnetCount)
        {
            label = ColumnTopicNames[labelSubnet];
            columnLabel = ColumnTopicLabels[labelSubnet];
        }
        Metrics.BeaconChainGossipReceivedByTopic.Increment(columnLabel ?? new StringLabel(label));

        if (Route(message.Topic, out string? topicName, out bool gloas, out bool column, out ulong subnetId) is { } drop)
        {
            return Drop(label, GossipDropReason.UnknownTopic, drop);
        }

        return column
            ? columns.Handle(subnetId, gloas, message.Data.ToByteArray(), verdict, source)
            : gossip.Handle(topicName!, gloas, message.Data.ToByteArray(), verdict);
    }

    /// <summary>Finds the router and fork of a message on <paramref name="topic"/>.</summary>
    /// <returns><c>null</c> when this node handles the topic; otherwise the verdict for a message on it.</returns>
    private MessageValidity? Route(string topic, out string? name, out bool gloas, out bool column, out ulong subnetId)
    {
        gloas = false;
        column = false;
        subnetId = 0;

        // phase0 p2p: MUST reject messages with an unknown topic.
        if (!GossipTopics.TryParse(topic, out byte[]? digest, out name) || !GossipTopics.IsEth2TopicName(name!))
        {
            return MessageValidity.Rejected;
        }

        column = GossipTopics.TryParseDataColumnSidecarTopicName(name!, out subnetId);
        if (column && subnetId >= Eip7594DasConstants.DataColumnSidecarSubnetCount)
        {
            return MessageValidity.Rejected;
        }

        return TryGetFork(digest!, out gloas) && (column || IsHandled(name!, gloas)) ? null : MessageValidity.Ignored;
    }

    private static string[] CreateColumnTopicNames()
    {
        string[] names = new string[Eip7594DasConstants.DataColumnSidecarSubnetCount];
        for (ulong subnet = 0; subnet < Eip7594DasConstants.DataColumnSidecarSubnetCount; subnet++)
        {
            names[subnet] = GossipTopics.DataColumnSidecarTopicName(subnet);
        }

        return names;
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

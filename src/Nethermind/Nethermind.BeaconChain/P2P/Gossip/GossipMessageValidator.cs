// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
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
/// Data column sidecars are accepted unchecked and reach <see cref="ColumnGossipRouter"/> through the topic event.
/// </para>
/// </remarks>
public sealed class GossipMessageValidator(GossipRouter gossip, BeaconChainSpec spec, SlotClock slotClock)
{
    private const string UnhandledTopicLabel = "unhandled";

    // altair p2p "Transitioning the gossip": pre-fork topics stay live for 2 epochs after a fork and post-fork topics are joined before it.
    private const ulong EpochsBeforeCurrent = 2;
    private const ulong EpochsAfterCurrent = 1;

    private volatile DigestWindow? _window;

    /// <summary>Validates <paramref name="message"/> and consumes it when it passes.</summary>
    /// <returns>
    /// <see cref="MessageValidity.Accepted"/> only for a data column sidecar topic; otherwise
    /// <see cref="MessageValidity.Rejected"/> or <see cref="MessageValidity.Ignored"/>, including for a consumed message.
    /// </returns>
    public MessageValidity Verify(Message message)
    {
        bool parsed = GossipTopics.TryParse(message.Topic, out byte[]? digest, out string? name);
        string label = parsed && IsHandledName(name!) ? name! : UnhandledTopicLabel;

        // StrictNoSign: the library checks this only after the validator runs.
        if (!message.Signature.IsEmpty)
        {
            return Drop(label, GossipDropReason.SignedMessage, MessageValidity.Rejected);
        }

        // phase0 p2p: MUST reject messages with an unknown topic.
        if (!parsed || !GossipTopics.IsEth2TopicName(name!))
        {
            return Drop(label, GossipDropReason.UnknownTopic, MessageValidity.Rejected);
        }

        if (GossipTopics.IsDataColumnSidecarTopicName(name!))
        {
            return MessageValidity.Accepted;
        }

        if (!TryGetFork(digest!, out bool gloas) || !IsHandled(name!, gloas))
        {
            return Drop(label, GossipDropReason.UnknownTopic, MessageValidity.Ignored);
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

    private sealed record DigestWindow(ulong Epoch, (byte[] Digest, bool Gloas)[] Digests)
    {
        public static DigestWindow Create(BeaconChainSpec spec, ulong epoch)
        {
            ulong first = epoch > EpochsBeforeCurrent ? epoch - EpochsBeforeCurrent : 0;
            (byte[] Digest, bool Gloas)[] digests = new (byte[], bool)[epoch + EpochsAfterCurrent - first + 1];
            for (ulong e = first; e <= epoch + EpochsAfterCurrent; e++)
            {
                digests[e - first] = (ForkDigest.Compute(spec, e), e >= spec.GloasForkEpoch);
            }

            return new DigestWindow(epoch, digests);
        }
    }
}

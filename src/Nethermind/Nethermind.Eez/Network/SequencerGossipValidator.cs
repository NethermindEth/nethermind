// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Eez.Network;

internal sealed class SequencerGossipValidator(SequencerHeadCodec codec, SequencerHeadTracker tracker, ITimestamper clock)
{
    internal event Action? HeadAccepted;

    internal MessageId MessageId(Message message) => new(codec.MessageId(message.Data.Span));

    internal MessageValidity Validate(Message message)
    {
        if (message.Topic != codec.Topic || message.HasFrom || message.HasSeqno || message.HasSignature || message.HasKey)
            return MessageValidity.Throttled;
        if (!codec.TryDecode(message.Data.Span, out VerifiedSequencerHead? head)) return MessageValidity.Throttled;
        SequencerHeadVerdict verdict = tracker.Observe(head, clock.UnixTime.Milliseconds);
        if (verdict == SequencerHeadVerdict.Accept)
        {
            HeadAccepted?.Invoke();
            return MessageValidity.Accepted;
        }
        return verdict == SequencerHeadVerdict.Ignore ? MessageValidity.Ignored : MessageValidity.Rejected;
    }
}

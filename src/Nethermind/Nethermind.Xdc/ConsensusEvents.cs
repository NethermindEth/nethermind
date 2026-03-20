// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal interface IConsensusEvent { }

internal sealed class NewHeadEvent(XdcBlockHeader head) : IConsensusEvent
{
    public XdcBlockHeader Head { get; } = head;
}

internal sealed class VoteReceivedEvent(Vote vote) : IConsensusEvent
{
    public Vote Vote { get; } = vote;
}

internal sealed class TimeoutVoteReceivedEvent(Timeout timeout) : IConsensusEvent
{
    public Timeout Timeout { get; } = timeout;
}

internal sealed class SyncInfoReceivedEvent(SyncInfo info) : IConsensusEvent
{
    public SyncInfo Info { get; } = info;
}

internal sealed class TimeoutElapsedEvent : IConsensusEvent { }


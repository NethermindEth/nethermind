// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// A peer that can carry XDPoS 2.0 consensus messages, implemented by every XDC protocol version.
/// </summary>
internal interface IXdcConsensusPeer
{
    void SendVote(Vote vote);

    void SendTimeout(Timeout timeout);

    void SendSyncInfo(SyncInfo syncInfo);
}

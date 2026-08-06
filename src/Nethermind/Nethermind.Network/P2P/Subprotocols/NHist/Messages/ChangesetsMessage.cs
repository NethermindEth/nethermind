// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class ChangesetsMessage : NHistMessageBase
{
    public override int PacketType => NHist1MessageCode.Changesets;

    public IOwnedReadOnlyList<ChangesetChunkEntry> Chunks { get; set; } = ArrayPoolList<ChangesetChunkEntry>.Empty();

    public override void Dispose()
    {
        base.Dispose();
        Chunks.Dispose();
    }
}

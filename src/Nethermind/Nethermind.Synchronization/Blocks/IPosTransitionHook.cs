// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

public interface IPosTransitionHook
{

    // Needed to know what is the terminal block so in fast sync, for each
    // header, it calls this.
    void TryUpdateTerminalBlock(BlockHeader currentHeader);
    bool ImprovementRequirementSatisfied(PeerInfo? peerInfo);
    IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> headers);
}

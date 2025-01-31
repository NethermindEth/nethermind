// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

public interface IPosTransitionHook
{
    void TryUpdateTerminalBlock(BlockHeader currentHeader, bool shouldProcess);
    bool ImprovementRequirementSatisfied(PeerInfo? peerInfo);
    IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> headers);
}

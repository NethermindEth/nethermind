// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.State.Pbt.Migration;

/// <summary>The one rule that decides which native backend serves a scope during the EIP-8347 migration.</summary>
/// <remarks>
/// The backend is chosen by the block about to be executed (or, for a pure read, by the block whose state is
/// read): PBT once EIP-8347 is active, the flat MPT before. Pre-activation, PBT is kept in lockstep as a mirror
/// whenever it holds the base state. When it does not (the anchor import is still running, the BAL follower still
/// has to catch it up, or after a crash its persisted pointer is already above the base, which
/// <see cref="PbtDbManager.AddSnapshot"/> rejects), flat runs alone until PBT holds the base again.
/// </remarks>
internal sealed class MigrationBackendSelector(ISpecProvider specProvider, IPbtDbManager pbtManager)
{
    public bool IsBinary(BlockHeader? baseBlock, BlockHeader? targetBlock)
    {
        BlockHeader? header = targetBlock ?? baseBlock;
        return (header is null ? specProvider.GenesisSpec : specProvider.GetSpec(header)).IsEip8347Enabled;
    }

    public bool PbtHas(BlockHeader? baseBlock) => pbtManager.HasStateForBlock(new StateId(baseBlock));
}

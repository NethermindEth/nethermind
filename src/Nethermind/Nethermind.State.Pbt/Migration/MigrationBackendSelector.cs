// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.State.Pbt.Migration;

/// <summary>The one rule that decides which native backend serves a scope during the EIP-8347 migration.</summary>
/// <remarks>
/// The backend is chosen by the block about to be executed (or, for a pure read, by the block whose state is
/// read): PBT once EIP-8347 is active, the flat MPT before. Pre-activation, PBT is kept in lockstep as a mirror
/// whenever it holds the base state; when it does not, either the BAL follower still has to catch it up, or
/// (after a crash) its persisted pointer is already above the base, in which case flat runs alone until the
/// branch reaches it, since <see cref="PbtDbManager.AddSnapshot"/> rejects states below the pointer.
/// </remarks>
internal sealed class MigrationBackendSelector(ISpecProvider specProvider, IPbtDbManager pbtManager, PbtPersistenceCoordinator pbtPersistence)
{
    public bool IsBinary(BlockHeader? baseBlock, BlockHeader? targetBlock)
    {
        BlockHeader? header = targetBlock ?? baseBlock;
        return (header is null ? specProvider.GenesisSpec : specProvider.GetSpec(header)).IsEip8347Enabled;
    }

    public bool PbtHas(BlockHeader? baseBlock) => pbtManager.HasStateForBlock(new StateId(baseBlock));

    public bool PbtAhead(BlockHeader? baseBlock)
    {
        if (baseBlock is null) return false;
        StateId persisted = pbtPersistence.GetCurrentPersistedStateId();
        return persisted != StateId.PreGenesis && persisted.BlockNumber > baseBlock.Number;
    }
}

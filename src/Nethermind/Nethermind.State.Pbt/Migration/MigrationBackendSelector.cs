// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.State.Pbt.Migration;

/// <summary>The one rule that decides which native backend serves a scope during the EIP-8347 migration.</summary>
/// <remarks>
/// The backend is chosen by the block about to be executed (or, for a pure read, by the block whose state is
/// read): PBT once EIP-8347 is active, the flat MPT before. Pre-activation, main processing runs on flat alone and
/// the BAL followers bring PBT up to the blocks it processes (see <see cref="PbtBranchFollower"/>).
/// </remarks>
internal sealed class MigrationBackendSelector(ISpecProvider specProvider)
{
    public bool IsBinary(BlockHeader? baseBlock, BlockHeader? targetBlock)
    {
        BlockHeader? header = targetBlock ?? baseBlock;
        return (header is null ? specProvider.GenesisSpec : specProvider.GetSpec(header)).IsEip8347Enabled;
    }
}

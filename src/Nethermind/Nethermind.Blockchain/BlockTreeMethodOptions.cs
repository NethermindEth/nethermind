//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;

namespace Nethermind.Blockchain;

[Flags]
public enum BlockTreeLookupOptions
{
    None = 0,
    TotalDifficultyNotNeeded = 1,
    RequireCanonical = 2,
    DoNotCreateLevelIfMissing = 4,
    AllowInvalid = 8,
    All = 15
}

[Flags]
public enum BlockTreeInsertHeaderOptions
{
    None = 0,
    TotalDifficultyNotNeeded = 1,
    BeaconHeaderMetadata = 2,
    BeaconBodyMetadata = 4,
    NotOnMainChain = 8,
    MoveToBeaconMainChain = 16,

    BeaconBlockInsert = TotalDifficultyNotNeeded | BeaconHeaderMetadata | NotOnMainChain | BeaconBodyMetadata,
    BeaconHeaderInsert = BeaconHeaderMetadata | MoveToBeaconMainChain | NotOnMainChain
}

[Flags]
public enum BlockTreeInsertBlockOptions
{
    None = 0,
    SaveHeader = 1,
    SkipCanAcceptNewBlocks = 2  // If we have an invalid block, we're blocking the block tree. However, if we have old bodies/old receipts sync at the same time, we need this option. Otherwise, old bodies sync won't insert block, and we fail old receipts sync later
}

[Flags]
public enum BlockTreeSuggestOptions
{
    /// <summary>
    /// No options, just add to tree
    /// </summary>
    None = 0,

    /// <summary>
    /// If block should be processed.
    /// </summary>
    /// <remarks>
    /// If <see cref="ForceSetAsMain"/> and <see cref="ForceDontSetAsMain"/> are absent, then
    /// if <see cref="ShouldProcess"/> is set, block won't be set as main, if <see cref="ShouldProcess"/> is absent it will be set as main block.
    /// </remarks>
    ShouldProcess = 1,

    /// <summary>
    /// Add blocks during sync
    /// </summary>
    FillBeaconBlock = 2,

    /// <summary>
    /// Force to set as main block
    /// </summary>
    ForceSetAsMain = 4,

    /// <summary>
    /// Force not to set as main block
    /// </summary>
    ForceDontSetAsMain = 8,
}

public static class BlockTreeSuggestOptionsExtensions
{
    public static bool ContainsFlag(this BlockTreeSuggestOptions value, BlockTreeSuggestOptions flag) => (value & flag) == flag;
}

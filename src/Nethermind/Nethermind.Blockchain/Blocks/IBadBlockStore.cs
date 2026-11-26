// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain.Blocks;

public interface IBadBlockStore
{
    void Insert(Block block, WriteFlags writeFlags = WriteFlags.None);

    /// <remarks>
    /// A returned block can have <see cref="Block.EncodedBlockAccessList"/> set while <see cref="Block.BlockAccessList"/> is null.
    /// Decode the access list before the block goes back into validation or payload building.
    /// </remarks>
    IEnumerable<Block> GetAll();
}

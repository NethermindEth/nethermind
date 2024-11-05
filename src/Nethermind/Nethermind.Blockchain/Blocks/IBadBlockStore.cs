// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain.Blocks;

public interface IBadBlockStore
{
    void Insert(Block block, WriteFlags writeFlags = WriteFlags.None);
    IEnumerable<Block> GetAll();
}

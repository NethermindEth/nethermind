// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm
{
    public interface IBlockHashProvider
    {
        Hash256? GetBlockHash(BlockHeader currentBlock, in long number);
    }
}

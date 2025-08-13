// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public interface IBlockhashProvider
    {
        Hash256? GetBlockhash(BlockHeader currentBlock, long number);
        Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec? spec);
    }
}

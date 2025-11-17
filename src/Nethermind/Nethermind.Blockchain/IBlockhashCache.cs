// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

public interface IBlockhashCache
{
    Hash256? GetHash(BlockHeader headBlock, int depth);
    Task Prefetch(BlockHeader blockHeader);
}

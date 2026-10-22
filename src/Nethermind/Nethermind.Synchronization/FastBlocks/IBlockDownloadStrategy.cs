// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks;

public interface IBlockDownloadStrategy
{
    bool ShouldDownloadBlock(BlockInfo info);
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public static class BlockProcessingConstants
    {
        // Keep long-running processing and related write batches bounded.
        public const int MaxUncommittedBlocks = 64;
    }
}

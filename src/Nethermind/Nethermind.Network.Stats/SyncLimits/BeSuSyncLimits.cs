// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.SyncLimits
{
    public static class BeSuSyncLimits
    {
        public const int MaxHeaderFetch = 512; // Amount of block headers to be fetched per retrieval request
        public const int MaxBodyFetch = 128;   // Amount of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = 256; // Amount of transaction receipts to allow fetching per request
    }
}

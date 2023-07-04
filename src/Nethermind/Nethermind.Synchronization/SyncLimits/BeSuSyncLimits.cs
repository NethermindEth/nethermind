// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.SyncLimits
{
    public static class BeSuSyncLimits
    {
        public const int MaxHeaderFetch = GethSyncLimits.MaxHeaderFetch; // Amount of block headers to be fetched per retrieval request
        public const int MaxBodyFetch = GethSyncLimits.MaxBodyFetch; // Amount of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = GethSyncLimits.MaxReceiptFetch; // Amount of transaction receipts to allow fetching per request
    }
}

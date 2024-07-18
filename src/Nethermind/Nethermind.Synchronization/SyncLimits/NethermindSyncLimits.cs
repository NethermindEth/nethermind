// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.SyncLimits
{
    public static class NethermindSyncLimits
    {
        public const int MaxHeaderFetch = 512; // Amount of block headers to be fetched per retrieval request
        public const int MaxBodyFetch = 256; // Amount of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = 256; // Amount of transaction receipts to allow fetching per request
        public const int MaxCodeFetch = 1024; // Amount of contract codes to allow fetching per request
    }
}

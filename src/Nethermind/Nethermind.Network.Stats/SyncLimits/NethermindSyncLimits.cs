// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.SyncLimits
{
    public static class NethermindSyncLimits
    {
        public const int MaxHeaderFetch = 1024; // Number of block headers to be fetched per retrieval request
        public const int MaxBodyFetch = 512; // Number of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = 512; // Number of transaction receipts to allow fetching per request
        public const int MaxCodeFetch = 1024; // Number of contract codes to allow fetching per request
        public static int MaxHashesFetch = 10000; // Number of hashes to allow fetching per request
    }
}

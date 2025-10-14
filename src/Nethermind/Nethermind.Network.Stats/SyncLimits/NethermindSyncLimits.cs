// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.SyncLimits
{
    public static class NethermindSyncLimits
    {
        public const int MaxHeaderFetch = 512; // Number of block headers to be fetched per retrieval request
        public const int MaxBodyFetch = 256; // Number of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = 256; // Number of transaction receipts to allow fetching per request
        public const int MaxCodeFetch = 1024; // Number of contract codes to allow fetching per request
        public static int MaxHashesFetch = 4096; // Number of hashes to allow fetching per request
    }
}

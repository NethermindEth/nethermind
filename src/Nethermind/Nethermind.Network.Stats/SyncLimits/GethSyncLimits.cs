// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.SyncLimits
{
    public static class GethSyncLimits
    {
        public const int MaxHeaderFetch = 192; // Amount of block headers to be fetched per retrieval request
        public const int MaxBodyFetch = 128; // Amount of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = 128; // Amount of transaction receipts to allow fetching per request
        public const int MaxCodeFetch = 85; // Amount of contract codes (bytecode blobs) to allow fetching per request
        public const int MaxProofsFetch = 1024; // Trie node blobs in snap sync
        public const int MaxHelperTrieProofsFetch = 512; // Account/storage range request upper size in KB
        public const int MaxTxSend = 64; // Amount of transactions to be sent per request
        public const int MaxTxStatus = 256; // Amount of transactions to be queried per request
    }
}
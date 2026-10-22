// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.SyncLimits
{
    public static class GethSyncLimits
    {
        public const int MaxHeaderFetch = 192; // Amount of block headers to be fetched per retrieval request
        public const int MaxBodyFetch = 128; // Amount of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = 256; // Amount of transaction receipts to allow fetching per request
        public const int MaxCodeFetch = 64; // Amount of contract codes to allow fetching per request
        public const int MaxProofsFetch = 64; // Amount of merkle proofs to be fetched per retrieval request
        public const int MaxHelperTrieProofsFetch = 64; // Amount of helper tries to be fetched per retrieval request
        public const int MaxTxSend = 64; // Amount of transactions to be sent per request
        public const int MaxTxStatus = 256; // Amount of transactions to be queried per request
    }
}

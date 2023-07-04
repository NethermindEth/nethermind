// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public static class LesMessageCode
    {
        public const int Status = 0x00;
        public const int Announce = 0x01;
        public const int GetBlockHeaders = 0x02;
        public const int BlockHeaders = 0x03;
        public const int GetBlockBodies = 0x04;
        public const int BlockBodies = 0x05;
        public const int GetReceipts = 0x06;
        public const int Receipts = 0x07;
        public const int GetProofs = 0x08; // deprecated
        public const int Proofs = 0x09; // deprecated
        public const int GetContractCodes = 0x0a;
        public const int ContractCodes = 0x0b;
        public const int SendTx = 0x0c; // deprecated
        public const int GetHeaderProofs = 0x0d; // deprecated
        public const int HeaderProofs = 0x0e; // deprecated
        public const int GetProofsV2 = 0x0f;
        public const int ProofsV2 = 0x10;
        public const int GetHelperTrieProofs = 0x11;
        public const int HelperTrieProofs = 0x12;
        public const int SendTxV2 = 0x13;
        public const int GetTxStatus = 0x14;
        public const int TxStatus = 0x15;
        public const int Stop = 0x16;
        public const int Resume = 0x17;
    }
}

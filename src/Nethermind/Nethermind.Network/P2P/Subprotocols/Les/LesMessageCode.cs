//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

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

namespace Nethermind.Evm
{
    public static class GasCostOf
    {
        public const long Base = 2;
        public const long VeryLow = 3;
        public const long Low = 5;
        public const long Mid = 8;
        public const long High = 10;
        public const long ExtCode = 20;
        public const long ExtCodeEip150 = 700;
        public const long Balance = 20;
        public const long BalanceEip150 = 400;
        public const long BalanceEip1884 = 700;
        public const long SLoad = 50;
        public const long SLoadEip150 = 200;
        public const long SLoadEip1884 = 800;
        public const long JumpDest = 1;
        public const long SStoreNetMeteredEip1283 = 200;
        public const long SStoreNetMeteredEip2200 = 800;
        public const long SSet = 20000;
        public const long SReset = 5000;
        public const long Create = 32000;
        public const long CodeDeposit = 200;
        public const long Call = 40;
        public const long CallEip150 = 700;
        public const long CallValue = 9000;
        public const long CallStipend = 2300;
        public const long NewAccount = 25000;
        public const long Exp = 10;
        public const long ExpByte = 10;
        public const long ExpByteEip160 = 50;
        public const long Memory = 3;
        public const long TxCreate = 32000;
        public const long TxDataZero = 4;
        public const long TxDataNonZero = 68;
        public const long TxDataNonZeroEip2028 = 16;
        public const long Transaction = 21000;
        public const long Log = 375;
        public const long LogTopic = 375;
        public const long LogData = 8;
        public const long Sha3 = 30;
        public const long Sha3Word = 6;
        public const long BlockHash = 20;
        public const long SelfDestruct = 0;
        public const long SelfDestructEip150 = 5000;
        public const long ExtCodeHash = 400;
        public const long ExtCodeHashEip1884 = 700;
        public const long SelfBalance = 5;
        
        public const long ColdSLoad = 2100; // eip-2929
        public const long ColdAccountAccess = 2600; // eip-2929
        public const long WarmStateRead = 100; // eip-2929
        
        public const long AccessAccountListEntry = 2400; // eip-2930
        public const long AccessStorageListEntry = 1900; // eip-2930
    }
}

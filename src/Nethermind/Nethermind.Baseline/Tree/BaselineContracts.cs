//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Abi;

namespace Nethermind.Baseline.Tree
{
    public static class ContractMerkleTree
    {
        public static readonly AbiSignature InsertLeafAbiSig = new AbiSignature("insertLeaf",
            new AbiBytes(32)); // leafValue
        
        public static AbiSignature InsertLeavesAbiSig = new AbiSignature("insertLeaves",
            new AbiArray(new AbiBytes(32))); // leafValues
    }

    public static class ContractShield
    {
        public static readonly AbiSignature VerifyAndPushSig = new AbiSignature("verifyAndPush",
            new AbiArray(new AbiUInt(256)),
            new AbiArray(new AbiUInt(256)),
            new AbiBytes(32)); // verifyAndPush
    }
}

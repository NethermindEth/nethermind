// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

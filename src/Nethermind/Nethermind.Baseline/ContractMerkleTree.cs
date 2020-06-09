using Nethermind.Abi;

namespace Nethermind.Baseline
{
    public static class ContractMerkleTree
    {
        public static readonly AbiSignature InsertLeafAbiSig = new AbiSignature("insertLeaf",
            new AbiBytes(32)); // leafValue
        
        public static AbiSignature InsertLeavesAbiSig = new AbiSignature("insertLeaves",
            new AbiArray(new AbiBytes(32))); // leafValues
    }
}
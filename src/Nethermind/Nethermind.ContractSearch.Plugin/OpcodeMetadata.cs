
using System.Collections.Frozen;
using Nethermind.Evm;

// taken from il-evm
public struct OpcodeMetadata(long gasCost, byte additionalBytes, byte stackBehaviorPop, byte stackBehaviorPush, bool requiresStaticEnvToBeFalse = false)
{
    // these values are just indicators that these opcodes have extra gas handling
    private const int DYNAMIC = 0;
    private const int FREE = 0;
    private const int MEMORY_EXPANSION = 0;
    private const int ACCOUNT_ACCESS = 0;
    /// <summary>
    /// The gas cost.
    /// </summary>
    public long GasCost { get; } = gasCost;

    /// <summary>
    /// How many following bytes does this instruction have.
    /// </summary>
    public byte AdditionalBytes { get; } = additionalBytes;

    /// <summary>
    /// How many bytes are popped by this instruction.
    /// </summary>
    public byte StackBehaviorPop { get; } = stackBehaviorPop;

    /// <summary>
    /// How many bytes are pushed by this instruction.
    /// </summary>
    public byte StackBehaviorPush { get; } = stackBehaviorPush;

    /// <summary>
    /// requires EvmState.IsStatic to be false
    /// </summary>
    public bool IsNotStaticOpcode { get; } = requiresStaticEnvToBeFalse;

    public static readonly FrozenDictionary<Instruction, OpcodeMetadata> Operations =
        new Dictionary<Instruction, OpcodeMetadata>()
        {
            [Instruction.POP] = new(GasCostOf.Base, 0, 1, 0),
            [Instruction.STOP] = new(FREE, 0, 0, 0),
            [Instruction.PC] = new(GasCostOf.Base, 0, 0, 1),

            [Instruction.PUSH0] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.PUSH1] = new(GasCostOf.VeryLow, 1, 0, 1),
            [Instruction.PUSH2] = new(GasCostOf.VeryLow, 2, 0, 1),
            [Instruction.PUSH3] = new(GasCostOf.VeryLow, 3, 0, 1),
            [Instruction.PUSH4] = new(GasCostOf.VeryLow, 4, 0, 1),
            [Instruction.PUSH5] = new(GasCostOf.VeryLow, 5, 0, 1),
            [Instruction.PUSH6] = new(GasCostOf.VeryLow, 6, 0, 1),
            [Instruction.PUSH7] = new(GasCostOf.VeryLow, 7, 0, 1),
            [Instruction.PUSH8] = new(GasCostOf.VeryLow, 8, 0, 1),
            [Instruction.PUSH9] = new(GasCostOf.VeryLow, 9, 0, 1),
            [Instruction.PUSH10] = new(GasCostOf.VeryLow, 10, 0, 1),
            [Instruction.PUSH11] = new(GasCostOf.VeryLow, 11, 0, 1),
            [Instruction.PUSH12] = new(GasCostOf.VeryLow, 12, 0, 1),
            [Instruction.PUSH13] = new(GasCostOf.VeryLow, 13, 0, 1),
            [Instruction.PUSH14] = new(GasCostOf.VeryLow, 14, 0, 1),
            [Instruction.PUSH15] = new(GasCostOf.VeryLow, 15, 0, 1),
            [Instruction.PUSH16] = new(GasCostOf.VeryLow, 16, 0, 1),
            [Instruction.PUSH17] = new(GasCostOf.VeryLow, 17, 0, 1),
            [Instruction.PUSH18] = new(GasCostOf.VeryLow, 18, 0, 1),
            [Instruction.PUSH19] = new(GasCostOf.VeryLow, 19, 0, 1),
            [Instruction.PUSH20] = new(GasCostOf.VeryLow, 20, 0, 1),
            [Instruction.PUSH21] = new(GasCostOf.VeryLow, 21, 0, 1),
            [Instruction.PUSH22] = new(GasCostOf.VeryLow, 22, 0, 1),
            [Instruction.PUSH23] = new(GasCostOf.VeryLow, 23, 0, 1),
            [Instruction.PUSH24] = new(GasCostOf.VeryLow, 24, 0, 1),
            [Instruction.PUSH25] = new(GasCostOf.VeryLow, 25, 0, 1),
            [Instruction.PUSH26] = new(GasCostOf.VeryLow, 26, 0, 1),
            [Instruction.PUSH27] = new(GasCostOf.VeryLow, 27, 0, 1),
            [Instruction.PUSH28] = new(GasCostOf.VeryLow, 28, 0, 1),
            [Instruction.PUSH29] = new(GasCostOf.VeryLow, 29, 0, 1),
            [Instruction.PUSH30] = new(GasCostOf.VeryLow, 30, 0, 1),
            [Instruction.PUSH31] = new(GasCostOf.VeryLow, 31, 0, 1),
            [Instruction.PUSH32] = new(GasCostOf.VeryLow, 32, 0, 1),

            [Instruction.JUMPDEST] = new(GasCostOf.JumpDest, 0, 0, 0),
            [Instruction.JUMP] = new(GasCostOf.Mid, 0, 1, 0),
            [Instruction.JUMPI] = new(GasCostOf.High, 0, 2, 0),

            [Instruction.DUP1] = new(GasCostOf.VeryLow, 0, 1, 2),
            [Instruction.DUP2] = new(GasCostOf.VeryLow, 0, 2, 3),
            [Instruction.DUP3] = new(GasCostOf.VeryLow, 0, 3, 4),
            [Instruction.DUP4] = new(GasCostOf.VeryLow, 0, 4, 5),
            [Instruction.DUP5] = new(GasCostOf.VeryLow, 0, 5, 6),
            [Instruction.DUP6] = new(GasCostOf.VeryLow, 0, 6, 7),
            [Instruction.DUP7] = new(GasCostOf.VeryLow, 0, 7, 8),
            [Instruction.DUP8] = new(GasCostOf.VeryLow, 0, 8, 9),
            [Instruction.DUP9] = new(GasCostOf.VeryLow, 0, 9, 10),
            [Instruction.DUP10] = new(GasCostOf.VeryLow, 0, 10, 11),
            [Instruction.DUP11] = new(GasCostOf.VeryLow, 0, 11, 12),
            [Instruction.DUP12] = new(GasCostOf.VeryLow, 0, 12, 13),
            [Instruction.DUP13] = new(GasCostOf.VeryLow, 0, 13, 14),
            [Instruction.DUP14] = new(GasCostOf.VeryLow, 0, 14, 15),
            [Instruction.DUP15] = new(GasCostOf.VeryLow, 0, 15, 16),
            [Instruction.DUP16] = new(GasCostOf.VeryLow, 0, 16, 17),

            [Instruction.SWAP1] = new(GasCostOf.VeryLow, 0, 2, 2),
            [Instruction.SWAP2] = new(GasCostOf.VeryLow, 0, 3, 3),
            [Instruction.SWAP3] = new(GasCostOf.VeryLow, 0, 4, 4),
            [Instruction.SWAP4] = new(GasCostOf.VeryLow, 0, 5, 5),
            [Instruction.SWAP5] = new(GasCostOf.VeryLow, 0, 6, 6),
            [Instruction.SWAP6] = new(GasCostOf.VeryLow, 0, 7, 7),
            [Instruction.SWAP7] = new(GasCostOf.VeryLow, 0, 8, 8),
            [Instruction.SWAP8] = new(GasCostOf.VeryLow, 0, 9, 9),
            [Instruction.SWAP9] = new(GasCostOf.VeryLow, 0, 10, 10),
            [Instruction.SWAP10] = new(GasCostOf.VeryLow, 0, 11, 11),
            [Instruction.SWAP11] = new(GasCostOf.VeryLow, 0, 12, 12),
            [Instruction.SWAP12] = new(GasCostOf.VeryLow, 0, 13, 13),
            [Instruction.SWAP13] = new(GasCostOf.VeryLow, 0, 14, 14),
            [Instruction.SWAP14] = new(GasCostOf.VeryLow, 0, 15, 15),
            [Instruction.SWAP15] = new(GasCostOf.VeryLow, 0, 16, 16),
            [Instruction.SWAP16] = new(GasCostOf.VeryLow, 0, 17, 17),

            [Instruction.ADD] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.MUL] = new(GasCostOf.Low, 0, 2, 1),
            [Instruction.SUB] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.DIV] = new(GasCostOf.Low, 0, 2, 1),
            [Instruction.SDIV] = new(GasCostOf.Low, 0, 2, 1),
            [Instruction.MOD] = new(GasCostOf.Low, 0, 2, 1),
            [Instruction.SMOD] = new(GasCostOf.Low, 0, 2, 1),
            [Instruction.EXP] = new(GasCostOf.Exp, 0, 2, 1),
            [Instruction.SIGNEXTEND] = new(GasCostOf.Low, 0, 2, 1),
            [Instruction.LT] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.GT] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.SLT] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.SGT] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.EQ] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.ISZERO] = new(GasCostOf.VeryLow, 0, 1, 1),
            [Instruction.AND] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.OR] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.XOR] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.NOT] = new(GasCostOf.VeryLow, 0, 1, 1),
            [Instruction.ADDMOD] = new(GasCostOf.Mid, 0, 3, 1),
            [Instruction.MULMOD] = new(GasCostOf.Mid, 0, 3, 1),
            [Instruction.SHL] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.SHR] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.SAR] = new(GasCostOf.VeryLow, 0, 2, 1),
            [Instruction.BYTE] = new(GasCostOf.VeryLow, 0, 2, 1),

            [Instruction.KECCAK256] = new(GasCostOf.Sha3 + MEMORY_EXPANSION, 0, 2, 1),
            [Instruction.ADDRESS] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.BALANCE] = new(DYNAMIC + ACCOUNT_ACCESS, 0, 1, 1), // we need call GetBalanceCost in ILCompiler
            [Instruction.ORIGIN] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.CALLER] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.CALLVALUE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.CALLDATALOAD] = new(GasCostOf.VeryLow, 0, 1, 1),
            [Instruction.CALLDATASIZE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.CALLDATACOPY] = new(GasCostOf.VeryLow + MEMORY_EXPANSION, 0, 3, 0),
            [Instruction.CODESIZE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.CODECOPY] = new(GasCostOf.VeryLow + MEMORY_EXPANSION, 0, 3, 0),
            [Instruction.GASPRICE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.EXTCODESIZE] = new(DYNAMIC, 0, 1, 1),
            [Instruction.EXTCODECOPY] = new(DYNAMIC + MEMORY_EXPANSION + ACCOUNT_ACCESS, 0, 4, 0),
            [Instruction.RETURNDATASIZE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.RETURNDATACOPY] = new(GasCostOf.VeryLow + MEMORY_EXPANSION, 0, 3, 0),
            [Instruction.EXTCODEHASH] = new(DYNAMIC, 0, 1, 1),

            [Instruction.BLOCKHASH] = new(GasCostOf.BlockHash, 0, 1, 1),
            [Instruction.COINBASE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.TIMESTAMP] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.NUMBER] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.PREVRANDAO] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.GASLIMIT] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.CHAINID] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.SELFBALANCE] = new(GasCostOf.SelfBalance, 0, 0, 1),
            [Instruction.BASEFEE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.BLOBHASH] = new(GasCostOf.BlobHash, 0, 1, 1),
            [Instruction.BLOBBASEFEE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.INVALID] = new(GasCostOf.Base, 0, 0, 0),

            [Instruction.MLOAD] = new(GasCostOf.VeryLow + MEMORY_EXPANSION, 0, 1, 1),
            [Instruction.MSTORE] = new(GasCostOf.VeryLow + MEMORY_EXPANSION, 0, 2, 0),
            [Instruction.MSTORE8] = new(GasCostOf.VeryLow + MEMORY_EXPANSION, 0, 2, 0),
            [Instruction.PC] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.MSIZE] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.GAS] = new(GasCostOf.Base, 0, 0, 1),
            [Instruction.MCOPY] = new(GasCostOf.VeryLow + MEMORY_EXPANSION, 0, 3, 0),

            [Instruction.LOG0] = new(GasCostOf.Log + MEMORY_EXPANSION, 0, 2, 0, true),
            [Instruction.LOG1] = new(GasCostOf.Log + MEMORY_EXPANSION, 0, 3, 0, true),
            [Instruction.LOG2] = new(GasCostOf.Log + MEMORY_EXPANSION, 0, 4, 0, true),
            [Instruction.LOG3] = new(GasCostOf.Log + MEMORY_EXPANSION, 0, 5, 0, true),
            [Instruction.LOG4] = new(GasCostOf.Log + MEMORY_EXPANSION, 0, 6, 0, true),

            [Instruction.TLOAD] = new(GasCostOf.TLoad, 0, 1, 1),
            [Instruction.TSTORE] = new(GasCostOf.TStore, 0, 2, 0, true),

            [Instruction.SLOAD] = new(DYNAMIC, 0, 1, 1),
            [Instruction.SSTORE] = new(DYNAMIC, 0, 2, 0, true),

            [Instruction.CREATE] = new(GasCostOf.Create + DYNAMIC, 0, 3, 1, true),
            [Instruction.CREATE2] = new(GasCostOf.Create + DYNAMIC, 0, 4, 1, true),

            // in theory Call opcodes apart CALLCODE require isStatic but it depends on their args so not worth amortizing
            [Instruction.CALL] = new(DYNAMIC, 0, 7, 1),
            [Instruction.CALLCODE] = new(DYNAMIC, 0, 7, 1),
            [Instruction.DELEGATECALL] = new(DYNAMIC, 0, 6, 1),
            [Instruction.STATICCALL] = new(DYNAMIC, 0, 6, 1),
            [Instruction.SELFDESTRUCT] = new(GasCostOf.SelfDestruct + DYNAMIC, 0, 1, 0, true),

            [Instruction.RETURN] = new(MEMORY_EXPANSION, 0, 2, 0), // has memory costs
            [Instruction.REVERT] = new(MEMORY_EXPANSION, 0, 2, 0), // has memory costs
        }.ToFrozenDictionary();
    public static OpcodeMetadata GetMetadata(Instruction instruction) => OpcodeMetadata.Operations.GetValueOrDefault(instruction, OpcodeMetadata.Operations[Instruction.INVALID]);

}

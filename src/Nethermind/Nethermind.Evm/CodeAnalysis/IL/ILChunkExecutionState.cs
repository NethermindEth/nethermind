
using System;

namespace Nethermind.Evm.CodeAnalysis.IL;

public enum ContractState
{
    NotStarting = 0, // initial state
    Running = 1 << 0, // running currect contract
    Halted = 1 << 1, // halted execution to execute CALL or CREATE opcode
    Finished = 1 << 2, // successful execution
    Failed = 1 << 3,// failed execution due to EvmException
    Return = 1 << 4,// failed execution due to EvmException
    Revert = 1 << 5,// failed execution due to EvmException
    Jumping = 1 << 6, // internal method is jumping to another segment
}
public struct ILChunkExecutionState()
{
    public readonly bool ShouldAbort => ContractState switch
    {
        ContractState.Finished => true,
        ContractState.Failed => true,
        ContractState.Return => true,
        ContractState.Revert => true,
        _ => false
    };
    public readonly bool ShouldJump => ContractState == ContractState.Jumping;
    public readonly bool ShouldHalt => ContractState == ContractState.Halted;

    public ReadOnlyMemory<byte> ReturnData;

    public int JumpDestination;

    public ContractState ContractState;

    public EvmState CallResult;
    public EvmExceptionType ExceptionType;
}

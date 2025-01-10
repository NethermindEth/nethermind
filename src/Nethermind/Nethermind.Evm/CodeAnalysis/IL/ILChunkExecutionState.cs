
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
    EPHEMERAL_JUMP = 1 << 7,
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
    //ShouldFail || ShouldReturn || ShouldStop || ShouldRevert;
    public readonly bool ShouldFail => ExceptionType != EvmExceptionType.None;

    public ContractState ContractState;

    public EvmState CallResult;
    public EvmExceptionType ExceptionType;
}

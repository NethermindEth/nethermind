// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Specs.Forks;

namespace MachineState.Actions
{
    public record MoveNext : ActionsBase;
    public record MoveBack : ActionsBase;
    public record Goto(int index) : ActionsBase;
    public record BytecodeInserted(string bytecode) : ActionsBase;
    public record BytecodeInsertedB(byte[] bytecode) : ActionsBase;
    public record CallDataInserted(string calldata) : ActionsBase;
    public record FileLoaded(string filePath) : ActionsBase;
    public record TracesLoaded(string filePath) : ActionsBase;
    public record UpdateState(GethLikeTxTrace traces) : ActionsBase;
    public record SetForkChoice(IReleaseSpec forkName) : ActionsBase;
    public record ThrowError(string error) : ActionsBase;
    public record SetGasMode(bool ignore, long gasValue) : ActionsBase;
    public record RunBytecode : ActionsBase;

}

namespace Nethermind.Evm.Lab
{
    public class MachineState : GethLikeTxTrace, IState<MachineState>
    {
        public MachineState(GethLikeTxTrace trace)
            => SetState(trace, true);

        public MachineState()
        {
            AvailableGas = VirtualMachineTestsBase.DefaultBlockGasLimit;
            SelectedFork = Cancun.Instance;
        }

        public MachineState SetState(GethLikeTxTrace trace, bool isInit = false)
        {
            Entries = trace.Entries;
            ReturnValue = trace.ReturnValue;
            Failed = trace.Failed;
            Index = 0;
            Depth = 0;
            EventsSink.EmptyQueue();
            if (isInit)
            {
                AvailableGas = VirtualMachineTestsBase.DefaultBlockGasLimit;
                SelectedFork = Cancun.Instance;
            }
            return this;
        }

        public GethTxTraceEntry Current => base.Entries[Index];
        public int Index { get; private set; }
        public int Depth { get; private set; }
        public long AvailableGas { get; set; }
        public IReleaseSpec SelectedFork { get; set; }

        public byte[] Bytecode { get; set; }
        public byte[] CallData { get; set; }

        public MachineState Next()
        {
            Index = (Index + 1) % base.Entries.Count;
            return this;
        }
        public MachineState Previous()
        {
            Index = (Index - 1) < 0 ? base.Entries.Count - 1 : Index - 1;
            return this;
        }
        public MachineState Goto(int index)
        {
            Index = index % base.Entries.Count;
            return this;
        }

        public MachineState SetDepth(int depth)
        {
            Depth = depth;
            return this;
        }

        public MachineState SetGas(long gas)
        {
            AvailableGas = gas;
            return this;
        }

        public MachineState SetFork(IReleaseSpec forkname)
        {
            SelectedFork = forkname;
            return this;
        }


        IState<MachineState> IState<MachineState>.Initialize(MachineState seed) => seed;
    }
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.GethStyle;


namespace MachineState.Actions
{
    public record MoveNext : ActionsBase;
    public record MoveBack : ActionsBase;
    public record Goto(int index) : ActionsBase;
    public record FileSelected(string filePath) : ActionsBase;
    public record BytecodeInserted(string filePath) : ActionsBase;
    public record CallDataInserted(string filePath) : ActionsBase;
}

namespace Nethermind.Evm.Lab
{
    public class MachineState : GethLikeTxTrace, IState<MachineState>
    {
        public MachineState(GethLikeTxTrace trace)
        {
            Entries = trace.Entries;
            ReturnValue = trace.ReturnValue;
            Failed = trace.Failed;
        }

        public MachineState() { }


        public Queue<ActionsBase> Events { get; set; } = new();
        public GethTxTraceEntry Current => base.Entries[Index];
        public int Index { get; private set; }
        public int Depth { get; private set; }

        public string Bytecode { get; set; }
        public string CallData { get; set; }

        public MachineState Next()
        {
            Index = (Index + 1) % base.Entries.Count;
            return this;
        }
        public MachineState Previous()
        {
            Index = (Index - 1) % base.Entries.Count;
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

        IState<MachineState> IState<MachineState>.Initialize(MachineState seed) => seed;
    }
}

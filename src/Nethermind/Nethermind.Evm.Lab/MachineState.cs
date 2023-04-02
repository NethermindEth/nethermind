// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.GethStyle;

namespace MachineState.Actions
{
    public record MoveNext : ActionsBase;
    public record MoveBack : ActionsBase;
    public record Goto(int index) : ActionsBase;
    public record BytecodeInserted(string bytecode) : ActionsBase;
    public record CallDataInserted(string calldata) : ActionsBase;
    public record FileLoaded(string filePath) : ActionsBase;
    public record TracesLoaded(string filePath) : ActionsBase;
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


        private Stopwatch watch = new Stopwatch();
        private Queue<ActionsBase> Events { get; set; } = new();


        public GethTxTraceEntry Current => base.Entries[Index];
        public int Index { get; private set; }
        public int Depth { get; private set; }

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

        public void EnqueueEvent(ActionsBase msg)
        {
            if (!watch.IsRunning || watch.ElapsedMilliseconds > 100)
            {
                Events.Enqueue(msg);
                if (!watch.IsRunning) watch.Start();
                else watch.Restart();
            }
        }

        public bool TryDequeueEvent(out ActionsBase? msg)
        {
            if (Events.Any())
            {
                msg = Events.Dequeue();
                return true;
            }
            else
            {
                msg = null;
                return false;
            }
        }

        IState<MachineState> IState<MachineState>.Initialize(MachineState seed) => seed;
    }
}

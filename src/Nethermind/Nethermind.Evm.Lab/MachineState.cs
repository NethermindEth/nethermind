// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using DotNetty.Common.Utilities;
using MachineStateEvents;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab.Components;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Lab.Parser;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace MachineStateEvents
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
    public record Reset : ActionsBase;

}

namespace Nethermind.Evm.Lab
{
    public class MachineState : GethLikeTxTrace, IState<MachineState>
    {

        public EthereumRestrictedInstance context = new(Cancun.Instance);
        public GethLikeTxMemoryTracer Tracer => new(new GethTraceOptions
        {
            EnableMemory = true
        });
        public MachineState Initialize(bool swapStateInPlace = false)
        {
            byte[] bytecode = Core.Extensions.Bytes.FromHexString(Uri.IsWellFormedUriString(GlobalState.initialCmdArgument, UriKind.Absolute) ? File.OpenText(GlobalState.initialCmdArgument).ReadToEnd() : GlobalState.initialCmdArgument);

            RuntimeContext = new CodeInfo(bytecode);
            CallData = Array.Empty<byte>();
            var resultTraces = context.ExecuteBytecode(Tracer, long.MaxValue, bytecode).BuildResult();
            if (!swapStateInPlace)
                EventsSink.EnqueueEvent(new UpdateState(resultTraces), true);
            else SetState(resultTraces);
            return this;
        }
        public MachineState(GethLikeTxTrace trace)
            => SetState(trace, true);

        public MachineState()
        {
            AvailableGas = VirtualMachineTestsBase.DefaultBlockGasLimit;
            SelectedFork = Cancun.Instance;
        }

        public EventsSink EventsSink { get; } = new EventsSink();

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
                RuntimeContext = new CodeInfo(BytecodeParser.ExtractBytecodeFromTrace(trace));
                CallData = Array.Empty<byte>();
            }
            return this;
        }

        public GethTxTraceEntry Current => base.Entries[Index];
        public int Index { get; private set; }
        public int Depth { get; private set; }
        public long AvailableGas { get; set; }
        public IReleaseSpec SelectedFork { get; set; }
        public CodeInfo RuntimeContext { get; set; }
        public byte[] CallData { get; set; }

        public MachineState Next()
        {
            Index = (Index + 1) == base.Entries.Count ? base.Entries.Count - 1 : Index + 1;
            return this;
        }
        public MachineState Previous()
        {
            Index = (Index - 1) < 0 ? 0 : Index - 1;
            return this;
        }
        public MachineState Goto(int index)
        {
            if (index >= base.Entries.Count) Index = base.Entries.Count - 1;
            else Index = index;
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

        public async Task<bool> MoveNext()
        {
            if (this.EventsSink.TryDequeueEvent(out var currentEvent))
            {
                lock (this)
                {
                    try
                    {
                        MachineState.Update(this, currentEvent).GetState();
                    }
                    catch (Exception ex)
                    {
                        var dialogView = MainView.ShowError(ex.Message,
                            () =>
                            {
                                this.EventsSink.EnqueueEvent(new Reset());
                            }
                        );
                    }
                }
                return true;
            }
            return false;
        }

        public static IState<MachineState> Update(IState<MachineState> state, ActionsBase msg)
        {
            switch (msg)
            {
                case Goto idxMsg:
                    return state.GetState().Goto(idxMsg.index);
                case MoveNext _:
                    return state.GetState().Next();
                case MoveBack _:
                    return state.GetState().Previous();
                case FileLoaded flMsg:
                    {
                        var file = File.OpenText(flMsg.filePath);
                        if (file == null)
                        {
                            state.EventsSink.EnqueueEvent(new ThrowError($"File {flMsg.filePath} not found"), true);
                            break;
                        }

                        state.EventsSink.EnqueueEvent(new BytecodeInserted(file.ReadToEnd()), true);

                        break;
                    }
                case BytecodeInserted biMsg:
                    {
                        state.EventsSink.EnqueueEvent(new BytecodeInsertedB(Nethermind.Core.Extensions.Bytes.FromHexString(biMsg.bytecode)), true);
                        break;
                    }
                case BytecodeInsertedB biMsg:
                    {
                        state.GetState().RuntimeContext = new CodeInfo(biMsg.bytecode);
                        state.EventsSink.EnqueueEvent(new RunBytecode(), true);
                        return state;
                    }
                case CallDataInserted ciMsg:
                    {
                        var calldata = Nethermind.Core.Extensions.Bytes.FromHexString(ciMsg.calldata);
                        state.GetState().CallData = calldata;
                        break;
                    }
                case UpdateState updState:
                    {
                        if (updState.traces.Failed)
                        {
                            state.EventsSink.EnqueueEvent(new ThrowError($"Transaction Execution Failed"), true);
                            break;
                        }
                        return state.GetState().SetState(updState.traces);
                    }
                case SetForkChoice frkMsg:
                    {
                        state.GetState().context = new(frkMsg.forkName);
                        state.EventsSink.EnqueueEvent(new RunBytecode(), true);
                        return state.GetState().SetFork(frkMsg.forkName);
                    }
                case SetGasMode gasMsg:
                    {
                        state.GetState().SetGas(gasMsg.ignore ? int.MaxValue : gasMsg.gasValue);
                        state.EventsSink.EnqueueEvent(new RunBytecode(), true);
                        break;
                    }
                case RunBytecode _:
                    {
                        var localTracer = state.GetState().Tracer;
                        state.GetState().context.ExecuteBytecode(localTracer, state.GetState().AvailableGas, state.GetState().RuntimeContext.MachineCode);
                        state.EventsSink.EnqueueEvent(new UpdateState(localTracer.BuildResult()), true);
                        break;
                    }
                case Reset _:
                    {
                        return state.GetState().Initialize();
                    }
                case ThrowError errMsg:
                    {
                        throw new Exception(errMsg.error);
                    }
            }
            return state;
        }
    }
}

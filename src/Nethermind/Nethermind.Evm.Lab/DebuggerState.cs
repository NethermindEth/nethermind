// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using DotNetty.Common.Utilities;
using DebuggerStateEvents;
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
using Nethermind.Evm.Tracing.Debugger;
using Nethermind.Evm;
using MachineStateEvents;
using FluentAssertions.Equivalency.Tracing;
using System;

namespace DebuggerStateEvents
{
    public record MoveNext(bool onlyOneStep) : ActionsBase;
    public record SetBreakpoint(int depth, int pc, Func<EvmState, bool> condition = null, bool unsetBreakpoint = false) : ActionsBase;
    public record SetGlobalCheck(Func<EvmState, bool> condition = null) : ActionsBase;
    public record UpdateStack(int stackIndex, byte[] newBytes, bool isPop) : ActionsBase;
    public record Start : ActionsBase;
    public record Lock : ActionsBase;
    public record Update : ActionsBase;
    public record Reset : ActionsBase;
    public record Abort : ActionsBase;

}

namespace Nethermind.Evm.Lab
{
    public class DebuggerState : IState<DebuggerState>
    {
        public EthereumRestrictedInstance context = new(Cancun.Instance);
        public DebugTracer Tracer = new(new GethLikeTxMemoryTracer(GethTraceOptions.Default));
        public DebuggerState Initialize(long? gasAvailable = null, IReleaseSpec? spec = null, byte[]? bytecode = null)
        {
            return SetFork(spec ?? Cancun.Instance)
                .SetBytecode(bytecode ?? Bytes.FromHexString(Uri.IsWellFormedUriString(GlobalState.initialCmdArgument, UriKind.Absolute) ? File.OpenText(GlobalState.initialCmdArgument).ReadToEnd() : GlobalState.initialCmdArgument))
                .SetGas(gasAvailable ?? VirtualMachineTestsBase.DefaultBlockGasLimit)
                .ResetTracer(true)
                .Setup(); ;
        }
        public DebuggerState() => Initialize();

        public EventsSink EventsSink { get; } = new EventsSink();
        private Thread WorkThread { get; set; }
        public IReleaseSpec SelectedFork { get; set; }
        public CodeInfo RuntimeContext { get; set; }
        public long AvailableGas { get; private set; }
        public bool IsActive => Tracer.CanReadState;

        public DebuggerState Setup()
        {
            WorkThread = new Thread(() =>
            {
                try
                {
                    context.ExecuteBytecode(Tracer, AvailableGas, RuntimeContext.MachineCode);
                }
                catch
                {
                    Console.WriteLine("Thread Stopped");
                }
                finally
                {
                    EventsSink.EnqueueEvent(new Update());
                }
            });
            return this;
        }
        public DebuggerState Start()
        {
            var start = (0, 0);
            Tracer.SetBreakPoint(start);
            WorkThread?.Start();
            return this;
        }
        public DebuggerState Next()
        {
            Tracer.MoveNext(executeOneStep: false);
            return this;
        }
        public DebuggerState Step()
        {
            Tracer.MoveNext(executeOneStep: true);
            return this;
        }
        public DebuggerState Abort()
        {
            Tracer.Abort();
            WorkThread?.Interrupt();
            return this;
        }
        public DebuggerState SetGas(long gas)
        {
            AvailableGas = gas;
            if (Tracer.CanReadState)
            {
                Tracer.CurrentState.GasAvailable = AvailableGas;
            }
            return this;
        }
        public DebuggerState SetFork(IReleaseSpec forkname)
        {
            SelectedFork = forkname;
            context = new(forkname);
            return this;
        }
        public DebuggerState SetBytecode(byte[] bytecode)
        {
            RuntimeContext = new CodeInfo(bytecode);
            return this;
        }
        public DebuggerState ResetTracer(bool hookEvent = false)
        {
            WorkThread?.Interrupt();
            Tracer.Reset(new GethLikeTxMemoryTracer(GethTraceOptions.Default));
            if (hookEvent)
            {
                Tracer.BreakPointReached += () =>
                {
                    EventsSink.EnqueueEvent(new Update(), true);
                };
            }
            return this;
        }
        public DebuggerState Reset()
        {
            context = new(Cancun.Instance);
            return this.ResetTracer();
        }


        IState<DebuggerState> IState<DebuggerState>.Initialize(DebuggerState seed) => seed;

        public async Task<bool> MoveNext()
        {
            if (this.EventsSink.TryDequeueEvent(out var currentEvent))
            {
                lock (this)
                {
                    try
                    {
                        DebuggerState.Update(this, currentEvent);
                    }
                    catch (Exception ex)
                    {
                        var dialogView = MainView.ShowError(ex.Message,
                            () =>
                            {
                                this.EventsSink.EnqueueEvent(new DebuggerStateEvents.Reset());
                            }
                        );
                    }
                }
                return true;
            }
            return false;
        }

        public static DebuggerState Update(DebuggerState state, ActionsBase msg)
        {
            switch (msg)
            {
                case DebuggerStateEvents.MoveNext nxtMsg:
                    return nxtMsg.onlyOneStep ? state.Step() : state.Next();
                case MachineStateEvents.BytecodeInserted biMsg:
                    {
                        state.EventsSink.EnqueueEvent(new BytecodeInsertedB(Bytes.FromHexString(biMsg.bytecode)), true);
                        break;
                    }
                case MachineStateEvents.BytecodeInsertedB biMsg:
                    {
                        return state
                            .Reset()
                            .SetBytecode(biMsg.bytecode)
                            .Setup();

                    }
                case MachineStateEvents.SetForkChoice frkMsg:
                    {
                        return state
                            .SetFork(frkMsg.forkName)
                            .ResetTracer()
                            .Setup();
                    }
                case MachineStateEvents.SetGasMode gasMsg:
                    {
                        return state
                            .SetGas(gasMsg.ignore ? int.MaxValue : gasMsg.gasValue);
                    }

                case DebuggerStateEvents.SetBreakpoint brkMsg:
                    {
                        var breakpoint = (brkMsg.depth, brkMsg.pc);
                        if (brkMsg.unsetBreakpoint)
                        {
                            state.Tracer.UnsetBreakPoint(breakpoint.depth, breakpoint.pc);
                        }
                        else
                        {
                            state.Tracer.SetBreakPoint(breakpoint, brkMsg.condition);
                        }
                        state.EventsSink.EnqueueEvent(new Update(), true);
                        break;
                    }
                case DebuggerStateEvents.SetGlobalCheck chkMsg:
                    {
                        state.Tracer.SetCondtion(chkMsg.condition);
                        break;
                    }

                case DebuggerStateEvents.Start _:
                    {
                        return state.Start();
                    }
                case DebuggerStateEvents.Reset _:
                    {
                        return state.Reset()
                            .Setup();
                    }
                case DebuggerStateEvents.Update _ or DebuggerStateEvents.Lock _:
                    {
                        return state;
                    }
                case DebuggerStateEvents.Abort _:
                    {
                        return state.Abort()
                            .Setup();
                    }
                case MachineStateEvents.ThrowError errMsg:
                    {
                        throw new Exception(errMsg.error);
                    }
                case DebuggerStateEvents.UpdateStack stkMsg:
                    {
                        byte[] memory = state.Tracer.CurrentState.DataStack;
                        int dataStackHead = state.Tracer.CurrentState.DataStackHead;
                        if (stkMsg.isPop)
                        {
                            // get subarray after item
                            byte[] afterBytes = memory.Slice(32 * (stkMsg.stackIndex + 1));
                            Array.Copy(afterBytes, 0, memory, 32 * stkMsg.stackIndex, afterBytes.Length);
                            dataStackHead--;
                        }
                        else
                        {
                            Array.Copy(stkMsg.newBytes, 0, memory, 32 * stkMsg.stackIndex, stkMsg.newBytes.Length);
                        }

                        state.Tracer.CurrentState.DataStack = memory;
                        state.Tracer.CurrentState.DataStackHead = dataStackHead;
                        return state;
                    }
            }
            return state;
        }
    }
}

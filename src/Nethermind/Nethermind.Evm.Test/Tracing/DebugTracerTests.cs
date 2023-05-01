// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing.DebugTrace;
using Nethermind.Evm.Tracing.GethStyle;
using System.Threading.Tasks;
using System;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using System.Text.Json;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.Test
{
    public class DebugTracerTests : VirtualMachineTestsBase
    {

        public GethLikeTxTracer GethLikeTxTracer => new GethLikeTxTracer(GethTraceOptions.Default);

        [TestCase("0x5b601760005600")]
        public void Debugger_Halts_Execution_On_Breakpoint(string bytecodeHex)
        {
            // this bytecode create an infinite loop that keeps pushing 0x17 to the stack so it is bound to stackoverflow (or even to use up its gas) i.e :  
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int JUMP_OPCODE_PTR_BREAK_POINT = 5;
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                // we activate GoToNextBreakpoint mode (i.e : deactivate StepByStepMode)
                IsStepByStepModeOn = false,
            };

            // we set the break point to BREAK_POINT
            tracer.SetBreakPoint(JUMP_OPCODE_PTR_BREAK_POINT);

            var vmTask = Task.Run(() => Execute(tracer, bytecode), cancellationTokenSource.Token);

            // we run the bytecode for <confidenceLevelDesired> iteration and check how many times we stopped at BREAK_POINT
            int confidenceLevelDesired = 100;
            int confidenceLevelReached = 0;
            int iterationsCount = 0;

            // Test fails if iterationsCount != confidenceLevelReached != confidenceLevelDesired
            bool TestFailed = false;

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    iterationsCount++;
                    confidenceLevelReached += tracer.CurrentState.ProgramCounter == JUMP_OPCODE_PTR_BREAK_POINT ? 1 : 0;

                    if (iterationsCount == confidenceLevelDesired)
                    {
                        TestFailed = confidenceLevelReached < confidenceLevelDesired;
                        cancellationTokenSource.Cancel();
                    }
                    tracer.MoveNext();
                }
            }

            Assert.False(TestFailed);
        }

        [TestCase("0x5b601760005600")]
        public void Debugger_Halts_Execution_On_Breakpoint_If_Condition_Is_True(string bytecodeHex)
        {
            // this bytecode create an infinite loop that keeps pushing 0x17 to the stack so it is bound to stackoverflow (or even to use up its gas) i.e :  
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int JUMP_OPCODE_PTR_BREAK_POINT = 5;
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                // we activate GoToNextBreakpoint mode (i.e : deactivate StepByStepMode)
                IsStepByStepModeOn = false,
            };

            // we set the break point to BREAK_POINT
            tracer.SetBreakPoint(JUMP_OPCODE_PTR_BREAK_POINT, state =>
            {
                return state.DataStackHead == 23;
            });

            var vmTask = Task.Run(() => Execute(tracer, bytecode), cancellationTokenSource.Token);

            // we run the bytecode for <confidenceLevelDesired> iteration and check how many times we stopped at BREAK_POINT
            int confidenceLevelDesired = 100;
            int confidenceLevelReached = 0;
            int iterationsCount = 0;

            // Test fails if confidenceLevelReached > 1
            bool TestFailed = false;

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    iterationsCount++;
                    confidenceLevelReached += tracer.CurrentState.ProgramCounter == JUMP_OPCODE_PTR_BREAK_POINT ? 1 : 0;

                    if (iterationsCount == confidenceLevelDesired)
                    {
                        TestFailed = confidenceLevelReached > 1;
                        cancellationTokenSource.Cancel();
                    }

                    tracer.MoveNext();
                }
            }

            Assert.False(TestFailed);
        }

        [TestCase("0x5b5b5b5b5b5b5b5b5b5b")]
        public void Debugger_Halts_Execution_On_Eeach_Iteration(string bytecodeHex)
        {
            // this bytecode is just a bunch of NOP/JUMPDEST, the idea is it will take as much bytes in the bytecode as steps to go throught it
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                // we activate step by step mode in tracer
                IsStepByStepModeOn = true,
            };

            var vmTask = Task.Run(() => Execute(tracer, bytecode));

            int countBreaks = 0;

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    // we count how many steps it took to run the bytecode
                    countBreaks++;

                    tracer.MoveNext();
                }
            }

            countBreaks--; //Post-run break

            // we check that it matches the number of opcodes in the bytecode
            Assert.AreEqual(countBreaks, bytecode.Length);
        }


        [TestCase("0x5b601760005600")]
        public void Debugger_Can_Alter_Program_Counter(string bytecodeHex)
        {
            // this bytecode create an infinite loop that keeps pushing 0x17 to the stack so it is bound to stackoverflow (or even to use up its gas) i.e :  
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int JUMP_OPCODE_PTR_BREAK_POINT = 5;
            DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = true,
            };

            var vmTask = Task.Run(() => Execute(tracer, bytecode));

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    // we override the pc to skip the JUMP and reach the STOP opcode causing a successful execution 
                    if (tracer.CurrentState.ProgramCounter == JUMP_OPCODE_PTR_BREAK_POINT)
                        tracer.CurrentState.ProgramCounter++;

                    tracer.MoveNext();
                }
            }

            // we check if bytecode execution failed
            var resultTraces = (tracer.InnerTracer as GethLikeTxTracer).BuildResult();
            Assert.IsFalse(resultTraces.Failed);
        }

        [TestCase("5b6017600160005700")]
        public void Debugger_Can_Alter_Data_Stack(string bytecodeHex)
        {
            // this bytecode create an infinite loop that keeps pushing 0x17 to the stack so it is bound to stackoverflow (or even to use up its gas) i.e :  
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int JUMP_OPCODE_PTR_BREAK_POINT = 5;
            DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = true,
            };

            var vmTask = Task.Run(() => Execute(tracer, bytecode));

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    // we pop the condition and overwrite it with a false to force breaking out of the loop
                    if (tracer.CurrentState.ProgramCounter == JUMP_OPCODE_PTR_BREAK_POINT)
                    {
                        EvmStack stack = new(tracer.CurrentState.DataStack, tracer.CurrentState.DataStackHead, tracer);
                        stack.PopLimbo();
                        stack.PushByte(0x00);
                    }

                    tracer.MoveNext();
                }
            }

            // we check if bytecode execution failed
            var resultTraces = (tracer.InnerTracer as GethLikeTxTracer).BuildResult();
            Assert.IsFalse(resultTraces.Failed);
        }

        [TestCase("6017806000526000511460005260206000f3")]
        public void Debugger_Can_Alter_Memory(string bytecodeHex)
        {
            // this bytecode create an infinite loop that keeps pushing 0x17 to the stack so it is bound to stackoverflow (or even to use up its gas) i.e :  
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int MSTORE_OPCODE_PTR_BREAK_POINT = 6;
            DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = true,
            };

            var vmTask = Task.Run(() => Execute(tracer, bytecode));

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {

                    // we alter the value stored in memory to force EQ check at the end to fail
                    if (tracer.CurrentState.ProgramCounter == MSTORE_OPCODE_PTR_BREAK_POINT)
                    {
                        tracer.CurrentState.Memory.SaveByte(31, 0x0A);
                    }

                    tracer.MoveNext();
                }
            }

            // we check if bytecode execution failed
            var resultTraces = (tracer.InnerTracer as GethLikeTxTracer).BuildResult();
            Assert.IsTrue(resultTraces.ReturnValue[31] == 0);
        }

        [TestCase("6017806000526000511460005260206000f3")]
        public void Use_Debug_Tracer_To_Check_Assertion_Live(string bytecodeHex)
        {
            // this bytecode create an infinite loop that keeps pushing 0x17 to the stack so it is bound to stackoverflow (or even to use up its gas) i.e :  
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int MSTORE_OPCODE_PTR_BREAK_POINT = 6;
            DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = true,
            };

            var vmTask = Task.Run(() => Execute(tracer, bytecode));

            long gasAvailable_pre_MSTORE = 0;
            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    // we alter the value stored in memory to force EQ check at the end to fail
                    if (tracer.CurrentState.ProgramCounter == MSTORE_OPCODE_PTR_BREAK_POINT)
                    {
                        gasAvailable_pre_MSTORE = tracer.CurrentState.GasAvailable;
                    }

                    if (tracer.CurrentState.ProgramCounter == MSTORE_OPCODE_PTR_BREAK_POINT + 1)
                    {
                        long gasAvailable_post_MSTORE = tracer.CurrentState.GasAvailable;
                        Assert.AreEqual(GasCostOf.VeryLow, gasAvailable_pre_MSTORE - gasAvailable_post_MSTORE);
                    }

                    tracer.MoveNext();
                }
            }
        }


        [TestCase("ef601700")]
        public void Use_Debug_Tracer_To_Check_failure_status(string bytecodeHex)
        {
            // this bytecode fails on first opcode INVALID
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = true,
            };

            var vmTask = Task.Run(() => Execute(tracer, bytecode));

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    tracer.MoveNext();
                }
            }

            // we check if bytecode execution failed
            var resultTraces = (tracer.InnerTracer as GethLikeTxTracer).BuildResult();
            Assert.IsTrue(resultTraces.Failed);
        }
    }
}

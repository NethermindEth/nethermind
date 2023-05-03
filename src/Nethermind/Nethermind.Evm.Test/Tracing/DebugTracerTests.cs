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

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            Machine.ForceDebuggerForTesting = true;
        }

        [TestCase("0x5b601760005600")]
        public void Debugger_Halts_Execution_On_Breakpoint(string bytecodeHex)
        {
            // this bytecode create an infinite loop that keeps pushing 0x17 to the stack so it is bound to stackoverflow (or even to use up its gas) i.e :  
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int JUMP_OPCODE_PTR_BREAK_POINT = 5;
            using DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                // we activate GoToNextBreakpoint mode (i.e : deactivate StepByStepMode)
                IsStepByStepModeOn = false,
            };

            // we set the break point to BREAK_POINT
            tracer.SetBreakPoint(JUMP_OPCODE_PTR_BREAK_POINT);

            // we set the break point to BREAK_POINT
            Thread vmThread = new Thread(() => Execute(tracer, bytecode));
            vmThread.Start();

            // we run the bytecode for <confidenceLevelDesired> iteration and check how many times we stopped at BREAK_POINT
            int confidenceLevelDesired = 100;
            int confidenceLevelReached = 0;
            int iterationsCount = 0;

            // Test fails if iterationsCount != confidenceLevelReached != confidenceLevelDesired
            bool TestFailed = false;

            while (vmThread.IsAlive)
            {
                if (tracer.CanReadState)
                {
                    iterationsCount++;
                    confidenceLevelReached += tracer.CurrentState.ProgramCounter == JUMP_OPCODE_PTR_BREAK_POINT ? 1 : 0;
                    if (iterationsCount == confidenceLevelDesired)
                    {
                        TestFailed = confidenceLevelReached < confidenceLevelDesired;
                        tracer.Abort();
                        vmThread.Interrupt();
                        break;
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
            using DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                // we activate GoToNextBreakpoint mode (i.e : deactivate StepByStepMode)
                IsStepByStepModeOn = false,
            };

            // we set the break point to BREAK_POINT
            tracer.SetBreakPoint(JUMP_OPCODE_PTR_BREAK_POINT, state =>
            {
                return state.DataStackHead == 23;
            });

            Thread vmThread = new(() => Execute(tracer, bytecode));
            vmThread.Start();

            // we run the bytecode for <confidenceLevelDesired> iteration and check how many times we stopped at BREAK_POINT
            int confidenceLevelDesired = 100;
            int confidenceLevelReached = 0;
            int iterationsCount = 0;

            // Test fails if confidenceLevelReached > 1
            bool TestFailed = false;

            while (vmThread.IsAlive)
            {
                if (tracer.CanReadState)
                {
                    iterationsCount++;
                    confidenceLevelReached += tracer.CurrentState.ProgramCounter == JUMP_OPCODE_PTR_BREAK_POINT ? 1 : 0;

                    if (iterationsCount == confidenceLevelDesired)
                    {
                        TestFailed = confidenceLevelReached > 1;

                        tracer.Abort();
                        vmThread.Interrupt();
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

            using DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                // we activate step by step mode in tracer
                IsStepByStepModeOn = true,
            };

            Thread vmThread = new Thread(() => Execute(tracer, bytecode));
            vmThread.Start();

            int countBreaks = 0;

            while (vmThread.IsAlive)
            {
                if (tracer.CanReadState)
                {
                    // we count how many steps it took to run the bytecode
                    countBreaks++;

                    tracer.MoveNext();
                }
            }

            countBreaks--; //Post-run break

            // we check that it matches the number of opcodes in the bytecode
            Assert.That(bytecode.Length, Is.EqualTo(countBreaks));
        }


        [TestCase("0x5b601760005600")]
        public void Debugger_Can_Alter_Program_Counter(string bytecodeHex)
        {
            // this bytecode create an infinite loop that keeps pushing 0x17 to the stack so it is bound to stackoverflow (or even to use up its gas) i.e :  
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int JUMP_OPCODE_PTR_BREAK_POINT = 5;
            using DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = true,
            };

            tracer.SetBreakPoint(JUMP_OPCODE_PTR_BREAK_POINT);

            Thread vmThread = new Thread(() => Execute(tracer, bytecode));
            vmThread.Start();

            while (vmThread.IsAlive)
            {
                if (tracer.CanReadState)
                {
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
            using DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = false,
            };

            tracer.SetBreakPoint(JUMP_OPCODE_PTR_BREAK_POINT);

            Thread vmThread = new Thread(() => Execute(tracer, bytecode));
            vmThread.Start();

            while (vmThread.IsAlive)
            {
                if (tracer.CanReadState)
                {
                    // we pop the condition and overwrite it with a false to force breaking out of the loop
                    EvmStack stack = new(tracer.CurrentState.DataStack, tracer.CurrentState.DataStackHead, tracer);
                    stack.PopLimbo();
                    stack.PushByte(0x00);

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
            using DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = false,
            };

            tracer.SetBreakPoint(MSTORE_OPCODE_PTR_BREAK_POINT);

            Thread vmThread = new Thread(() => Execute(tracer, bytecode));
            vmThread.Start();

            while (vmThread.IsAlive)
            {
                if (tracer.CanReadState)
                {
                    // we alter the value stored in memory to force EQ check at the end to fail
                    tracer.CurrentState.Memory.SaveByte(31, 0x0A);

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
            using DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = false,
            };

            tracer.SetBreakPoint(MSTORE_OPCODE_PTR_BREAK_POINT);
            tracer.SetBreakPoint(MSTORE_OPCODE_PTR_BREAK_POINT + 1);

            Thread vmThread = new Thread(() => Execute(tracer, bytecode));
            vmThread.Start();

            long? gasAvailable_pre_MSTORE = null;
            while (vmThread.IsAlive)
            {
                if (tracer.CanReadState)
                {
                    // we alter the value stored in memory to force EQ check at the end to fail
                    if (gasAvailable_pre_MSTORE is null) gasAvailable_pre_MSTORE = tracer.CurrentState.GasAvailable;
                    else
                    {
                        long gasAvailable_post_MSTORE = tracer.CurrentState.GasAvailable;
                        Assert.That(gasAvailable_pre_MSTORE - gasAvailable_post_MSTORE, Is.EqualTo(GasCostOf.VeryLow));
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

            using DebugTracer tracer = new DebugTracer(GethLikeTxTracer)
            {
                IsStepByStepModeOn = true,
            };

            Thread vmThread = new Thread(() => Execute(tracer, bytecode));
            vmThread.Start();

            while (vmThread.IsAlive)
            {
                if (tracer.CanReadState)
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

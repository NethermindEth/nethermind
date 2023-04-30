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
    public class DebuggerTracerTests : VirtualMachineTestsBase
    {
        [TestCase("0x5b601760005600")]
        public void Debugger_Halts_Execution_On_Breakpoint(string bytecodeHex)
        {
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            const int BREAK_POINT = 5;
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            DebugTracer tracer = new DebugTracer(GethTraceOptions.Default)
            {
                IsStepByStepModeOn = false,
            };

            tracer.SetBreakPoint(BREAK_POINT);

            var vmTask = Task.Run(() => Execute(tracer, bytecode), cancellationTokenSource.Token);

            int confidenceLevelDesired = 100;
            int confidenceLevelReached = 0;
            int iterationsCount = 0;

            bool TestFailed = false;

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    iterationsCount++;
                    confidenceLevelReached += tracer.CurrentState.ProgramCounter == BREAK_POINT ? 1 : 0;

                    if(iterationsCount == confidenceLevelDesired)
                    {
                        TestFailed = confidenceLevelReached < confidenceLevelDesired;
                        cancellationTokenSource.Cancel();
                    }
                    tracer.MoveNext = true;
                }
            }

            Assert.False(TestFailed);
        }

        [TestCase("0x5b5b5b5b5b5b5b5b5b5b")]
        public void Debugger_Halts_Execution_On_Eeach_Iteration(string bytecodeHex)
        {
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);

            DebugTracer tracer = new DebugTracer(GethTraceOptions.Default)
            {
                IsStepByStepModeOn = true,
            };

            tracer.SetBreakPoint(5);

            var vmTask = Task.Run(() => Execute(tracer, bytecode));

            int countBreaks = 0;

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    countBreaks++;
                    tracer.MoveNext = true;

                }
            }

            countBreaks--; //Pre-run break
            countBreaks--; //Post-run break

            Assert.AreEqual(countBreaks, bytecode.Length);
        }


        [TestCase("0x5b601760005600")]
        public void Debugger_Can_Alter_Program_Counter(string bytecodeHex)
        {
            byte[] bytecode = Bytes.FromHexString(bytecodeHex);
            DebugTracer tracer = new DebugTracer(GethTraceOptions.Default)
            {
                IsStepByStepModeOn = true,
            };

            var vmTask = Task.Run(() => Execute(tracer, bytecode));

            while (!vmTask.IsCompleted)
            {
                if (tracer.CanReadState())
                {
                    if(tracer.CurrentState.ProgramCounter == 5)
                        tracer.CurrentState.ProgramCounter++;
                    tracer.MoveNext = true;
                }
            }
        }
    }
}

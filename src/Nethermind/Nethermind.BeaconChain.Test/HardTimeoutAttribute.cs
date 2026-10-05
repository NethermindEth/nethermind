// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Commands;

namespace Nethermind.BeaconChain.Test;

/// <summary>Signals alone cannot stop synchronous code; the abandoned test thread runs until process exit.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class HardTimeoutAttribute(int milliseconds) : NUnitAttribute, IWrapTestMethod
{
    public TestCommand Wrap(TestCommand command) => new HardTimeoutCommand(command, milliseconds);

    private sealed class HardTimeoutCommand(TestCommand inner, int milliseconds) : DelegatingTestCommand(inner)
    {
        public override TestResult Execute(TestExecutionContext context)
        {
            Task<TestResult> run = Task.Factory.StartNew(() => innerCommand.Execute(context), TaskCreationOptions.LongRunning);
            if (run.Wait(milliseconds))
            {
                return run.Result;
            }

            context.CurrentResult.SetResult(ResultState.Failure, $"Test exceeded the {milliseconds} ms hard timeout");
            return context.CurrentResult;
        }
    }
}

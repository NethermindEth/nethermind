// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Commands;

namespace Nethermind.BeaconChain.Test;

/// <summary>
/// Fails a test that runs longer than the limit. Unlike <see cref="CancelAfterAttribute"/>, which only signals a token,
/// it also stops waiting on synchronous code that never observes one, such as a selection loop that never fills.
/// </summary>
/// <remarks>The abandoned test keeps running on its own thread until the process exits.</remarks>
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

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MathNet.Numerics.Differentiation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps;
public abstract class InitStep
{
    public Task StepCompleted { private set; get; }

    private TaskCompletionSource _taskCompletedSource = new TaskCompletionSource();
    public InitStep()
    {
        StepCompleted = _taskCompletedSource.Task;
    }

    public async Task Execute(IEnumerable<Task> dependentSteps, CancellationToken cancellationToken)
    {
        cancellationToken.Register(() => _taskCompletedSource.TrySetCanceled());

        await Task.WhenAll(dependentSteps);
        await Setup(cancellationToken);
        SignalStepCompleted();
    }

    protected void SignalStepCompleted()
    {
        _taskCompletedSource.SetResult();
    }

    protected abstract Task Setup(CancellationToken cancellationToken);
}

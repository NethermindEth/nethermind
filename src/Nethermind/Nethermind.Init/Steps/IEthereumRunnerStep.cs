// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps
{
    public interface IStep
    {
        Task StepCompleted { get; }
        Task Execute(IEnumerable<Task> dependentSteps, CancellationToken cancellationToken);
        public bool MustInitialize => true;
    }
}

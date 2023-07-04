// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Init.Steps
{
    public enum StepInitializationStage
    {
        WaitingForDependencies,
        WaitingForExecution,
        Executing,
        Complete,
        Failed
    }
}

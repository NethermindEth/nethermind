// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Init.Steps;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    [RunnerStepDependencies(typeof(InitializeBlockchain))]
    public class StartBlockProcessorAuRa : StartBlockProcessor
    {
        public StartBlockProcessorAuRa(AuRaNethermindApi api) : base(api)
        {
        }
    }
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;

namespace Nethermind.Merge.AuRa;

public class AuraMergeModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<Plugin.MergeSealValidator>()
            .As<ISealValidator>();

        builder.RegisterType<AuraMergeBlockchainStack>()
            .As<AuraBlockchainStack>();
    }
}

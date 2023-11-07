// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;

namespace Nethermind.Merge.AuRa;

public class AuraMergeModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<Plugin.MergeSealValidator>()
            .As<ISealValidator>();
    }
}

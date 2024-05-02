// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Init.Steps;
using Module = Autofac.Module;

public class StepModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.RegisterIStepsFromAssembly(typeof(IStep).Assembly);
        builder.RegisterIStepsFromAssembly(GetType().Assembly);
    }
}

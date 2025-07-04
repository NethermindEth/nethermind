// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.State.OverridableEnv;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class AuRaTraceModuleFactory(IOverridableEnvFactory envFactory, AuraValidationModifier validationModifier, ILifetimeScope rootLifetimeScope) : TraceModuleFactory(envFactory, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureCommonBlockProcessing<T>(ContainerBuilder builder)
    {
        return base.ConfigureCommonBlockProcessing<T>(builder).AddModule(validationModifier);
    }
}

public class AuRaDebugModuleFactory(IOverridableEnvFactory envFactory, AuraValidationModifier validationModifier, ILifetimeScope rootLifetimeScope) : DebugModuleFactory(envFactory, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder)
    {
        return base.ConfigureTracerContainer(builder).AddModule(validationModifier);
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismModuleTests
{
    [TestCase(true, TestName = "CL enabled registers the CL startup step")]
    [TestCase(false, TestName = "CL disabled skips the CL startup step")]
    public void ClEnabled_gates_cl_registration(bool clEnabled)
    {
        ChainSpec chainSpec = new()
        {
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new OptimismChainSpecEngineParameters())
        };
        OptimismConfig config = new() { ClEnabled = clEnabled };

        ContainerBuilder builder = new();
        builder.RegisterModule(new OptimismModule(chainSpec, config));
        using IContainer container = builder.Build();

        bool clStepRegistered = container.Resolve<IEnumerable<StepInfo>>()
            .Any(step => step.StepType == typeof(StartOptimismCl));

        Assert.That(clStepRegistered, Is.EqualTo(clEnabled));
    }
}

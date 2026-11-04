// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Merge.Plugin;
using Nethermind.Taiko.Rpc;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class TaikoModuleRegistrationTests
{
    [Test]
    public void Both_virtual_machine_service_keys_resolve_to_the_same_Taiko_vm()
    {
        using IContainer container = BuildContainer();
        using ILifetimeScope scope = container.BeginLifetimeScope();

        IVirtualMachine nonGeneric = scope.Resolve<IVirtualMachine>();
        IVirtualMachine<EthereumGasPolicy> generic = scope.Resolve<IVirtualMachine<EthereumGasPolicy>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nonGeneric, Is.InstanceOf<TaikoEthereumVirtualMachine>());
            Assert.That(generic, Is.SameAs(nonGeneric));
        }
    }

    [Test]
    public void Engine_module_service_keys_resolve_to_the_same_Taiko_module()
    {
        using IContainer container = BuildContainer();

        ITaikoEngineRpcModule taikoModule = container.Resolve<ITaikoEngineRpcModule>();
        IEngineRpcModule engineModule = container.Resolve<IEngineRpcModule>();

        Assert.That(engineModule, Is.SameAs(taikoModule));
    }

    private static IContainer BuildContainer() => new ContainerBuilder()
        .AddModule(new TestNethermindModule())
        .AddSingleton(Substitute.For<IBlockFinder>())
        .AddModule(new TaikoModule())
        .AddSingleton(Substitute.For<IBlockhashProvider>())
        .AddSingleton(SpecProviderSubstitute.Create())
        .AddSingleton(Substitute.For<IL1OriginStore>())
        .AddSingleton(Substitute.For<IEngineRequestsTracker>())
        .Build();
}

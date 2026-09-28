// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class TaikoVirtualMachineRegistrationTests
{
    [Test]
    public void Both_virtual_machine_service_keys_resolve_to_the_same_Taiko_vm()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(Substitute.For<IBlockFinder>())
            .AddModule(new TaikoModule())
            .AddSingleton(Substitute.For<IBlockhashProvider>())
            .AddSingleton(SpecProviderSubstitute.Create())
            .AddSingleton(Substitute.For<IL1OriginStore>())
            .Build();
        using ILifetimeScope scope = container.BeginLifetimeScope();

        IVirtualMachine nonGeneric = scope.Resolve<IVirtualMachine>();
        IVirtualMachine<EthereumGasPolicy> generic = scope.Resolve<IVirtualMachine<EthereumGasPolicy>>();

        Assert.That(nonGeneric, Is.InstanceOf<TaikoEthereumVirtualMachine>());
        Assert.That(generic, Is.SameAs(nonGeneric));
    }
}

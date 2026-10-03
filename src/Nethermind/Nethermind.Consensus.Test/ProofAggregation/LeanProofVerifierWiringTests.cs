// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Crypto;
using Nethermind.Init.Steps;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Consensus.Test.ProofAggregation;

[NonParallelizable]
public class LeanProofVerifierWiringTests
{
    [Test]
    public async Task Startup_checks_a_decorated_backend_through_its_interface()
    {
        ILeanProofVerifier backend = Substitute.For<ILeanProofVerifier>();
        InclusionListProofVerifier decorator = new(backend);
        InitializeLeanBackend step = new(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), decorator);

        await step.Execute(CancellationToken.None);

        backend.Received(1).EnsureAvailable();
    }

    [Test]
    public void Startup_propagates_missing_backend_failure_through_a_decorator()
    {
        ILeanProofVerifier backend = Substitute.For<ILeanProofVerifier>();
        backend.When(verifier => verifier.EnsureAvailable()).Do(_ => throw new DllNotFoundException("lean backend missing"));
        InitializeLeanBackend step = new(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), new InclusionListProofVerifier(backend));

        Assert.That(() => step.Execute(CancellationToken.None), Throws.TypeOf<DllNotFoundException>());
    }

    [Test]
    public async Task Production_module_registers_native_verifier_without_loading_the_backend()
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create();
        Assert.That(chain.Container.Resolve<ILeanProofVerifier>(), Is.TypeOf<NativeLeanProofVerifier>());
        Assert.That(chain.BlockProcessor, Is.Not.Null);
        await chain.AddBlock();
    }

    [Test]
    public async Task Startup_leaves_native_library_unloaded_on_chains_without_the_fork()
    {
        InitializeLeanBackend step = new(new TestSingleReleaseSpecProvider(Amsterdam.Instance), NativeLeanProofVerifier.Instance);
        await step.Execute(CancellationToken.None);
    }

    [Test]
    public async Task Startup_preserves_an_explicitly_injected_backend()
    {
        FakeLeanProofVerifier verifier = new(true);
        InitializeLeanBackend step = new(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), verifier);
        await step.Execute(CancellationToken.None);
        Assert.That(verifier.VerificationCalls, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Production_and_validation_use_injected_verifier_only_after_activation(bool enabled)
    {
        FakeLeanProofVerifier verifier = new(true);
        OverridableReleaseSpec spec = new(Eip8288Prototype.Instance) { IsEip8288Enabled = enabled };
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(spec))
            .AddSingleton<ILeanProofVerifier>(verifier));

        Block block = await chain.AddBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Container.Resolve<ILeanProofVerifier>(), Is.SameAs(verifier));
            Assert.That(block.Header.RecursiveStark is not null, Is.EqualTo(enabled));
            Assert.That(verifier.ProofCalls > 0, Is.EqualTo(enabled));
            Assert.That(verifier.VerificationCalls > 0, Is.EqualTo(enabled));
        }
    }
}

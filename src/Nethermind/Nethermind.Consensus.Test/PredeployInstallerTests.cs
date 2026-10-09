// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class PredeployInstallerTests
{
    private static IEnumerable<TestCaseData> DeployedSystemContractCases()
    {
        foreach (bool noncanonicalPrestate in new[] { false, true })
        {
            yield return new TestCaseData(new Action<IReleaseSpec>(static spec => spec.IsEip8141Enabled.Returns(true)), Eip8141Constants.ExpiryVerifierAddress, noncanonicalPrestate)
                .SetName($"Eip8141_activation_does_not_write_the_expiry_verifier({noncanonicalPrestate})");
            yield return new TestCaseData(new Action<IReleaseSpec>(static spec => spec.IsEip8250Enabled.Returns(true)), Eip8250Constants.NonceManagerAddress, noncanonicalPrestate)
                .SetName($"Eip8250_activation_does_not_write_the_nonce_manager({noncanonicalPrestate})");
            yield return new TestCaseData(new Action<IReleaseSpec>(static spec => spec.IsEip8272Enabled.Returns(true)), Eip8272Constants.RecentRootAddress, noncanonicalPrestate)
                .SetName($"Eip8272_activation_does_not_write_the_recent_root({noncanonicalPrestate})");
        }
    }

    [TestCaseSource(nameof(DeployedSystemContractCases))]
    public void Activation_does_not_write_a_deployed_system_contract(Action<IReleaseSpec> activate, Address contract, bool noncanonicalPrestate)
    {
        (_, IWorldState writeState) = Install(
            activate,
            contract,
            nonce: noncanonicalPrestate ? 7UL : 0UL,
            code: noncanonicalPrestate ? [0x00] : []);

        Assert.That(writeState.ReceivedCalls(), Is.Empty);
    }

    private static (IReadOnlyStateProvider ReadState, IWorldState WriteState) Install(
        Action<IReleaseSpec> activate, Address predeploy, ulong nonce, byte[] code)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        activate(spec);

        IReadOnlyStateProvider readState = Substitute.For<IReadOnlyStateProvider>();
        readState.GetNonce(predeploy).Returns(nonce);
        readState.GetCode(predeploy).Returns(code);

        IWorldState writeState = Substitute.For<IWorldState>();
        PredeployInstaller.Install(readState, writeState, spec);

        return (readState, writeState);
    }
}

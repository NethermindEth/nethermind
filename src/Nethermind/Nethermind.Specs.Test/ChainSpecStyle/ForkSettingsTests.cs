// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
public class ForkSettingsTests
{
    [Test]
    public void Can_load_fork_settings_from_embedded_resource()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        forkSettings.Should().NotBeNull();
        forkSettings.Defaults.Should().NotBeNull();
        forkSettings.Contracts.Should().NotBeNull();
    }

    [Test]
    public void Default_parameters_have_correct_values()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        forkSettings.Defaults.GasLimitBoundDivisor.Should().Be(1024);
        forkSettings.Defaults.MaximumExtraDataSize.Should().Be(32);
        forkSettings.Defaults.MinGasLimit.Should().Be(5000);
        forkSettings.Defaults.MaxCodeSize.Should().Be(24576);
        forkSettings.Defaults.MinHistoryRetentionEpochs.Should().Be(82125);
        forkSettings.Defaults.MaxRlpBlockSize.Should().Be(8388608);
    }

    [Test]
    public void Contract_addresses_have_correct_values()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        forkSettings.BeaconRootsAddress.Should().Be(Eip4788Constants.BeaconRootsAddress);
        forkSettings.BlockHashHistoryAddress.Should().Be(Eip2935Constants.BlockHashHistoryAddress);
        forkSettings.WithdrawalRequestAddress.Should().Be(Eip7002Constants.WithdrawalRequestPredeployAddress);
        forkSettings.ConsolidationRequestAddress.Should().Be(Eip7251Constants.ConsolidationRequestPredeployAddress);
    }

    [Test]
    public void Cancun_fork_has_correct_eips()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var eips = forkSettings.GetForkEips("cancun");
        eips.Should().Contain(1153); // Transient storage
        eips.Should().Contain(4788); // Beacon block root
        eips.Should().Contain(4844); // Proto-danksharding
        eips.Should().Contain(5656); // MCOPY
        eips.Should().Contain(6780); // SELFDESTRUCT changes
    }

    [Test]
    public void Prague_fork_has_correct_eips()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var eips = forkSettings.GetForkEips("prague");
        eips.Should().Contain(2537); // BLS12-381 precompiles
        eips.Should().Contain(2935); // Block hash history
        eips.Should().Contain(6110); // Deposit requests
        eips.Should().Contain(7002); // Withdrawal requests
        eips.Should().Contain(7251); // Consolidation requests
        eips.Should().Contain(7623); // Calldata cost increase
        eips.Should().Contain(7702); // Set EOA code
    }

    [Test]
    public void Osaka_fork_has_correct_eips()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var eips = forkSettings.GetForkEips("osaka");
        eips.Should().Contain(7594); // PeerDAS
        eips.Should().Contain(7823); // EOF
        eips.Should().Contain(7934); // Max RLP block size
    }

    [Test]
    public void Cancun_blob_schedule_has_correct_values()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var blobSchedule = forkSettings.GetBlobSchedule("cancun");
        blobSchedule.Should().NotBeNull();
        blobSchedule!.Target.Should().Be(3);
        blobSchedule.Max.Should().Be(6);
        blobSchedule.BaseFeeUpdateFraction.Should().Be(3338477);
    }

    [Test]
    public void Prague_blob_schedule_has_correct_values()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var blobSchedule = forkSettings.GetBlobSchedule("prague");
        blobSchedule.Should().NotBeNull();
        blobSchedule!.Target.Should().Be(6);
        blobSchedule.Max.Should().Be(9);
        blobSchedule.BaseFeeUpdateFraction.Should().Be(5007716);
    }

    [Test]
    public void Bpo1_blob_schedule_has_correct_values()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var blobSchedule = forkSettings.GetBlobSchedule("bpo1");
        blobSchedule.Should().NotBeNull();
        blobSchedule!.Target.Should().Be(10);
        blobSchedule.Max.Should().Be(15);
        blobSchedule.BaseFeeUpdateFraction.Should().Be(8346193);
    }

    [Test]
    public void Bpo2_blob_schedule_has_correct_values()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var blobSchedule = forkSettings.GetBlobSchedule("bpo2");
        blobSchedule.Should().NotBeNull();
        blobSchedule!.Target.Should().Be(14);
        blobSchedule.Max.Should().Be(21);
        blobSchedule.BaseFeeUpdateFraction.Should().Be(11684671);
    }

    [Test]
    public void IsEipActiveAtFork_returns_correct_values()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        // EIP-1559 is activated at London
        forkSettings.IsEipActiveAtFork(1559, "london").Should().BeTrue();
        forkSettings.IsEipActiveAtFork(1559, "berlin").Should().BeFalse();

        // EIP-4844 is activated at Cancun
        forkSettings.IsEipActiveAtFork(4844, "cancun").Should().BeTrue();
        forkSettings.IsEipActiveAtFork(4844, "shanghai").Should().BeFalse();
        forkSettings.IsEipActiveAtFork(4844, "prague").Should().BeTrue(); // Still active after Cancun
    }

    [Test]
    public void GetForkEips_returns_empty_for_unknown_fork()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var eips = forkSettings.GetForkEips("unknown_fork");
        eips.Should().BeEmpty();
    }

    [Test]
    public void GetBlobSchedule_returns_null_for_fork_without_blob_schedule()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var blobSchedule = forkSettings.GetBlobSchedule("london");
        blobSchedule.Should().BeNull();
    }

    [Test]
    public void Fork_order_contains_all_major_forks()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        var forkOrder = forkSettings.GetForkOrder();
        forkOrder.Should().Contain("homestead");
        forkOrder.Should().Contain("byzantium");
        forkOrder.Should().Contain("london");
        forkOrder.Should().Contain("shanghai");
        forkOrder.Should().Contain("cancun");
        forkOrder.Should().Contain("prague");
        forkOrder.Should().Contain("osaka");
        forkOrder.Should().Contain("bpo1");
        forkOrder.Should().Contain("bpo2");
    }

    [Test]
    public void GetActivatingFork_returns_correct_fork_for_eips()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        forkSettings.GetActivatingFork(1559).Should().Be("london");
        forkSettings.GetActivatingFork(4844).Should().Be("cancun");
        forkSettings.GetActivatingFork(2537).Should().Be("prague");
        forkSettings.GetActivatingFork(7594).Should().Be("osaka");
        forkSettings.GetActivatingFork(150).Should().Be("tangerineWhistle");
    }

    [Test]
    public void GetActivatingFork_returns_null_for_unknown_eip()
    {
        ForkSettings forkSettings = ForkSettings.Instance;

        forkSettings.GetActivatingFork(99999).Should().BeNull();
    }
}

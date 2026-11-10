// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Taiko.TaikoSpec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class TaikoBlockValidatorTests
{
    [Test]
    public void Shasta_anchor_selector_accepts_signal_slots_only_when_enabled(
        [Values] bool useSignalSlotsSelector,
        [Values] bool signalSlotsEnabled)
    {
        TaikoShastaReleaseSpec spec = new() { IsAnchorV4WithSignalSlotsEnabled = signalSlotsEnabled };

        IHeaderValidator headerValidator = Substitute.For<IHeaderValidator>();
        headerValidator.Validate(default!, default!, default, out _, default).ReturnsForAnyArgs(true);

        TaikoBlockValidator validator = new(
            Always.Valid,
            headerValidator,
            Always.Valid,
            new TestSpecProvider(spec),
            Substitute.For<IEthereumEcdsa>(),
            LimboLogs.Instance);

        Transaction anchor = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(spec.TaikoL2Address)
            .WithData(useSignalSlotsSelector ? TaikoBlockValidator.AnchorV4WithSignalSlotsSelector : TaikoBlockValidator.AnchorV4Selector)
            .WithValue(0)
            .WithGasLimit(1_000_000)
            .WithMaxFeePerGas(1)
            .WithSenderAddress(TaikoBlockValidator.GoldenTouchAccount)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(1)
            .WithWithdrawals(0)
            .WithTransactions(anchor)
            .TestObject;

        bool isValid = validator.ValidateSuggestedBlock(block, Build.A.BlockHeader.TestObject, out string? error, validateHashes: false);

        Assert.That(isValid, Is.EqualTo(!useSignalSlotsSelector || signalSlotsEnabled), error);
    }
}

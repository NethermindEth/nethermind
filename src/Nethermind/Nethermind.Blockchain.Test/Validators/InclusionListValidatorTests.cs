// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Proofs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class InclusionListValidatorTests
{

    // [Test, MaxTime(Timeout.MaxTestTime)]
    // public void Not_null_withdrawals_are_invalid_pre_shanghai()
    // {
    //     ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, London.Instance));
    //     BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, _transactionProcessor, LimboLogs.Instance);
    //     bool isValid = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithWithdrawals(new Withdrawal[] { TestItem.WithdrawalA_1Eth, TestItem.WithdrawalB_2Eth }).TestObject);
    //     Assert.That(isValid, Is.False);
    // }
}

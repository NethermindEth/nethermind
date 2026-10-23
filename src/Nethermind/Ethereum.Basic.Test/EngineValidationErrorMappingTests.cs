// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

[TestFixture]
public class EngineValidationErrorMappingTests
{
    [TestCase("Transaction 0x208d5b37931979ba00ccd1275dd004ac06c4389d1b2b858cc12812070d9ca5f1 at index 0 failed with error max fee less than required max fee: address 0x92B42F831826417cc556C023E4d1D9cF2e833101, maxFee: 0, requiredMaxFee: 3616669")]
    public void Frame_tx_max_fee_below_required_maps_to_insufficient_max_fee(string validationError) =>
        Assert.That(BlockchainTestBase.MapValidationErrorsToEestExceptions(validationError), Does.Contain("TransactionException.INSUFFICIENT_MAX_FEE_PER_GAS"));
}

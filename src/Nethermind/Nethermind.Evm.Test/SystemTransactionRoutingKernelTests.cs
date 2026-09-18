// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class SystemTransactionRoutingKernelTests
{
    [Test]
    public void Route_and_option_decisions_match_the_production_contract_for_all_declared_flag_combinations()
    {
        const int declaredFlagMask = (int)(ExecutionOptions.Commit |
                                           ExecutionOptions.Restore |
                                           ExecutionOptions.SkipValidation |
                                           ExecutionOptions.Warmup |
                                           ExecutionOptions.BuildUp);

        for (int bits = 0; bits <= declaredFlagMask; bits++)
        {
            ExecutionOptions options = (ExecutionOptions)bits;
            bool expectedPayOriginalValue = (options & ExecutionOptions.SkipValidation) == 0;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    SystemTransactionRoutingKernel.UseSystemProcessor(isSystemTransaction: false, options),
                    Is.EqualTo(options == ExecutionOptions.SkipValidation),
                    $"normal route for {options}");
                Assert.That(
                    SystemTransactionRoutingKernel.UseSystemProcessor(isSystemTransaction: true, options),
                    Is.True,
                    $"system route for {options}");
                Assert.That(
                    SystemTransactionRoutingKernel.ShouldPayOriginalValue(options),
                    Is.EqualTo(expectedPayOriginalValue),
                    $"original-value decision for {options}");
            }

            ExecutionOptions effective = SystemTransactionRoutingKernel.GetSystemExecutionOptions(
                options,
                expectedPayOriginalValue);
            Assert.That(
                SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(effective, parallel: false),
                Is.False,
                $"system counter exclusion for {options}");
        }
    }

    [TestCase(ExecutionOptions.None, false, true)]
    [TestCase(ExecutionOptions.None, true, false)]
    [TestCase(ExecutionOptions.Commit, false, true)]
    [TestCase(ExecutionOptions.SkipValidation, false, false)]
    [TestCase(ExecutionOptions.SkipValidationAndCommit, false, false)]
    public void Counter_participation_depends_only_on_skip_validation_and_parallel(
        ExecutionOptions options,
        bool parallel,
        bool expected) =>
        Assert.That(
            SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(options, parallel),
            Is.EqualTo(expected));
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class GasCostTests
{
    [TestCaseSource(nameof(FjordL1CostCalculationTestCases))]
    public UInt256 Fjord_l1cost_should_match(UInt256 fastLzSize, UInt256 l1BaseFee, UInt256 blobBaseFee, UInt256 l1BaseFeeScalar, UInt256 l1BlobBaseFeeScalar) =>
        OptimismCostHelper.ComputeL1CostFjord(fastLzSize, l1BaseFee, blobBaseFee, l1BaseFeeScalar, l1BlobBaseFeeScalar, out _);

    public static IEnumerable FjordL1CostCalculationTestCases
    {
        get
        {
            static TestCaseData MakeTestCase(string testCase, ulong result, ulong fastLzSize, ulong l1BaseFee, ulong blobBaseFee, ulong l1BaseFeeScalar, ulong l1BlobBaseFeeScalar)
            {
                return new TestCaseData(new UInt256(fastLzSize), new UInt256(l1BaseFee), new UInt256(blobBaseFee), new UInt256(l1BaseFeeScalar), new UInt256(l1BlobBaseFeeScalar))
                {
                    ExpectedResult = new UInt256(result),
                    TestName = testCase
                };
            }

            yield return MakeTestCase("Low compressed size", 3203000, 50, 1000000000, 10000000, 2, 3);
            yield return MakeTestCase("Below minimal #1", 3203000, 150, 1000000000, 10000000, 2, 3);
            yield return MakeTestCase("Below minimal #2", 3203000, 170, 1000000000, 10000000, 2, 3);
            yield return MakeTestCase("Above minimal #1", 3217602, 171, 1000000000, 10000000, 2, 3);
            yield return MakeTestCase("Above minimal #2", 3994602, 200, 1000000000, 10000000, 2, 3);
            yield return MakeTestCase("Regular block #1", 2883950646753, 1044, 28549556977, 1, 7600, 862000);
        }
    }
}

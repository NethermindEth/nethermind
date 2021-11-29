//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class IntrinsicGasCalculatorTests
    {
        public static IEnumerable<(Transaction Tx, long cost, string Description)> TestCaseSource()
        {
            yield return (Build.A.Transaction.SignedAndResolved().TestObject, 21000, "empty");
        }

        public static IEnumerable<(List<object> orderQueue, long Cost)> AccessTestCaseSource()
        {
            yield return (new List<object> { }, 0);
            yield return (new List<object> {Address.Zero}, 2400);
            yield return (new List<object> {Address.Zero, (UInt256)1}, 4300);
            yield return (new List<object> {Address.Zero, (UInt256)1, TestItem.AddressA, (UInt256)1}, 8600);
            yield return (new List<object> {Address.Zero, (UInt256)1, Address.Zero, (UInt256)1}, 8600);
        }

        public static IEnumerable<(byte[] Data, int OldCost, int NewCost)> DataTestCaseSource()
        {
            yield return (new byte[] {0}, 4, 4);
            yield return (new byte[] {1}, 68, 16);
            yield return (new byte[] {0, 0, 1}, 76, 24);
            yield return (new byte[] {1, 1, 0}, 140, 36);
            yield return (new byte[] {0, 0, 1, 1}, 144, 40);
        }
        
        public static IEnumerable<(IReleaseSpec spec, bool supportsAccessLists, bool isAfterRepricing)> SpecInfoSource()
        {
            yield return (Homestead.Instance, false, false);
            yield return (Frontier.Instance, false, false);
            yield return (SpuriousDragon.Instance, false, false);
            yield return (TangerineWhistle.Instance, false, false);
            yield return (Byzantium.Instance, false, false);
            yield return (Constantinople.Instance, false, false);
            yield return (ConstantinopleFix.Instance, false, false);
            yield return (Istanbul.Instance, false, true);
            yield return (MuirGlacier.Instance, false, true);
            yield return (Berlin.Instance, true, true);
            yield return (London.Instance, true, true);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((Transaction Tx, long Cost, string Description) testCase)
        {
            IntrinsicGasCalculator.Calculate(testCase.Tx, Berlin.Instance).Should().Be(testCase.Cost);
        }
        
        [Test]
        public void Intrinsic_cost_is_calculated_properly_with_eip4488()
        {
            OverridableReleaseSpec spec = new(London.Instance) { IsEip4488Enabled = true };
            Transaction tx = Build.A.Transaction.WithData(Enumerable.Range(0, 200).Select(i => (byte)i).ToArray()).TestObject;
            IntrinsicGasCalculator.Calculate(tx, spec).Should().Be(GasCostOf.Transaction + GasCostOf.TxDataEip4488 * 200);
        }

        [Test]
        public void Intrinsic_cost_is_calculated_properly(
            [ValueSource(nameof(AccessTestCaseSource))] (List<object> orderQueue, long Cost) testCase,
            [ValueSource(nameof(SpecInfoSource))] (IReleaseSpec spec, bool supportsAccessLists, bool isAfterRepricing) specInfo)
        {
            AccessListBuilder accessListBuilder = new();
            foreach (object o in testCase.orderQueue)
            {
                if (o is Address address)
                {
                    accessListBuilder.AddAddress(address);
                }
                else if (o is UInt256 index)
                {
                    accessListBuilder.AddStorage(index);
                }
            }

            AccessList accessList = accessListBuilder.ToAccessList();
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithAccessList(accessList).TestObject;

            void Test(IReleaseSpec spec, bool supportsAccessLists)
            {
                if (!supportsAccessLists)
                {
                    Assert.Throws<InvalidDataException>(() => IntrinsicGasCalculator.Calculate(tx, spec));
                }
                else
                {
                    IntrinsicGasCalculator.Calculate(tx, spec).Should().Be(21000 + testCase.Cost, spec.Name);
                }
            }

            Test(specInfo.spec, specInfo.supportsAccessLists);
        }

        [Test]
        public void Intrinsic_cost_of_data_is_calculated_properly(
            [ValueSource(nameof(DataTestCaseSource))] (byte[] Data, int OldCost, int NewCost) testCase,
            [ValueSource(nameof(SpecInfoSource))] (IReleaseSpec spec, bool supportsAccessLists, bool isAfterRepricing) specInfo)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithData(testCase.Data).TestObject;

            void Test(IReleaseSpec spec, bool isAfterRepricing)
            {
                IntrinsicGasCalculator.Calculate(tx, spec).Should()
                    .Be(21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost), spec.Name,
                        testCase.Data.ToHexString());
            }

            Test(specInfo.spec, specInfo.isAfterRepricing);
        }
    }
}

//  Copyright (c) 2018 Demerzel Solutions Limited
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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class IntrinsicGasCalculatorTests
    {
        public static IEnumerable<(Transaction Tx, long cost, string Description)> TestCaseSource()
        {
            yield return (Build.A.Transaction.SignedAndResolved().TestObject, 21000, "empty");
        }
        
        public static IEnumerable<(int Addresses, int Storages, long Cost)> AccessTestCaseSource()
        {
            yield return (0, 0, 0);
            yield return (1, 0, 2400);
            yield return (1, 1, 4300);
            yield return (2, 2, 8600);
        }
        
        public static IEnumerable<(byte[] Data, int OldCost, int NewCost)> DataTestCaseSource()
        {
            yield return (new byte[] {0}, 4, 4);
            yield return (new byte[] {1}, 68, 16);
            yield return (new byte[] {0, 0, 1}, 76, 24);
            yield return (new byte[] {1, 1, 0}, 140, 36);
            yield return (new byte[] {0, 0, 1, 1}, 144, 40);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((Transaction Tx, long Cost, string Description) testCase)
        {
            IntrinsicGasCalculator.Calculate(testCase.Tx, Berlin.Instance).Should().Be(testCase.Cost);
        }
        
        [TestCaseSource(nameof(AccessTestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((int Addresses, int Storages, long Cost) testCase)
        {
            Dictionary<Address, IReadOnlySet<UInt256>> data = new();
            HashSet<UInt256> storages = new();
            
            for (int i = 0; i < testCase.Storages; i++)
            {
                storages.Add((UInt256)i);
            }
            
            for (int i = 0; i < testCase.Addresses; i++)
            {
                if (i == 0)
                {
                    data[TestItem.Addresses[i]] = storages;
                }
                else
                {
                    data[TestItem.Addresses[i]] = ImmutableHashSet<UInt256>.Empty;
                }
            }

            AccessList accessList = new(data);
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

            Test(Homestead.Instance, false);
            Test(Frontier.Instance, false);
            Test(SpuriousDragon.Instance, false);
            Test(TangerineWhistle.Instance, false);
            Test(Byzantium.Instance, false);
            Test(Constantinople.Instance, false);
            Test(ConstantinopleFix.Instance, false);
            Test(Istanbul.Instance, false);
            Test(MuirGlacier.Instance, false);
            Test(Berlin.Instance, true);
        }
        
        [TestCaseSource(nameof(DataTestCaseSource))]
        public void Intrinsic_cost_of_data_is_calculated_properly((byte[] Data, int OldCost, int NewCost) testCase)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithData(testCase.Data).TestObject;
            void Test(IReleaseSpec spec, bool isAfterRepricing)
            {
                IntrinsicGasCalculator.Calculate(tx, spec).Should()
                    .Be(21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost), spec.Name, testCase.Data.ToHexString());
            }

            Test(Homestead.Instance, false);
            Test(Frontier.Instance, false);
            Test(SpuriousDragon.Instance, false);
            Test(TangerineWhistle.Instance, false);
            Test(Byzantium.Instance, false);
            Test(Constantinople.Instance, false);
            Test(ConstantinopleFix.Instance, false);
            Test(Istanbul.Instance, true);
            Test(MuirGlacier.Instance, true);
            Test(Berlin.Instance, true);
        }
    }
}

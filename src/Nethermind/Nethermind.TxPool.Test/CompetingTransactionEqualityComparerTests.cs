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

using System.Collections;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool.Comparison;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    public class CompetingTransactionEqualityComparerTests
    {
        public static IEnumerable TestCases
        {
            get
            {
                var transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).TestObject;
                
                yield return new TestCaseData(null, null) {ExpectedResult = true};
                
                yield return new TestCaseData(transaction, null)
                {
                    ExpectedResult = false
                };
                
                yield return new TestCaseData(null, transaction)
                {
                    ExpectedResult = false
                };
                
                yield return new TestCaseData(transaction, Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(2).TestObject)
                {
                    ExpectedResult = false
                };
                
                yield return new TestCaseData(transaction, Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(4).TestObject)
                {
                    ExpectedResult = false
                };
                
                yield return new TestCaseData(transaction, transaction)
                {
                    ExpectedResult = true
                };
                
                yield return new TestCaseData(transaction,  Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).TestObject)
                {
                    ExpectedResult = true
                };
            }
        }
        
        [TestCaseSource(nameof(TestCases))]
        public bool Equals_test(Transaction t1, Transaction t2) => CompetingTransactionEqualityComparer.Instance.Equals(t1, t2);

        [TestCaseSource(nameof(TestCases))]
        public bool HashCode_test(Transaction t1, Transaction t2) =>
            CompetingTransactionEqualityComparer.Instance.GetHashCode(t1) == CompetingTransactionEqualityComparer.Instance.GetHashCode(t2);
    }
}

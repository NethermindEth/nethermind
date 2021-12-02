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

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class MinGasPriceContractTxFilterTests
    {
        public static IEnumerable IsAllowedTestCases
        {
            get
            {
                yield return new TestCaseData(TestItem.AddressA, 2ul).Returns(AcceptTxResultCodes.FeeTooLow).SetName("Filtered by contract.");
                yield return new TestCaseData(TestItem.AddressB, 0ul).Returns(AcceptTxResultCodes.FeeTooLow).SetName("Filtered by base limit.");
                yield return new TestCaseData(TestItem.AddressA, 5ul).Returns(AcceptTxResultCodes.Accepted).SetName("Allowed by contract.");
                yield return new TestCaseData(TestItem.AddressB, 1ul).Returns(AcceptTxResultCodes.Accepted).SetName("Allowed by base limit.");
            }
        }
        
        [TestCaseSource(nameof(IsAllowedTestCases))]
        public AcceptTxResultCodes is_allowed_returns_correct(Address address, ulong gasLimit)
        {
            IMinGasPriceTxFilter minGasPriceFilter = new MinGasPriceTxFilter(UInt256.One, Substitute.For<ISpecProvider>());
            IDictionaryContractDataStore<TxPriorityContract.Destination> dictionaryContractDataStore = Substitute.For<IDictionaryContractDataStore<TxPriorityContract.Destination>>();
            dictionaryContractDataStore.TryGetValue(
                    Arg.Any<BlockHeader>(),
                    Arg.Is<TxPriorityContract.Destination>(d => d.Target == TestItem.AddressA),
                    out Arg.Any<TxPriorityContract.Destination>())
                .Returns(x =>
                {
                    x[2] = new TxPriorityContract.Destination(TestItem.AddressA, Array.Empty<byte>(), 5);
                    return true;
                });
            
            MinGasPriceContractTxFilter txFilter = new(minGasPriceFilter, dictionaryContractDataStore);
            Transaction tx = Build.A.Transaction.WithTo(address).WithGasPrice(gasLimit).WithData(null).TestObject;

            return txFilter.IsAllowed(tx, Build.A.BlockHeader.TestObject).Code;
        }
    }
}

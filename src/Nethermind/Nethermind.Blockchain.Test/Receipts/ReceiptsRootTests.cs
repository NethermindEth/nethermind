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

using System.Collections;
using System.Collections.Generic;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts
{
    public class ReceiptsRootTests
    {
        public static IEnumerable ReceiptsRootTestCases
        {
            get
            {
                Keccak skipHash = new Keccak("0x8f4aebb6fea8f70b5fb5fcc578d8ad7584caed6f662b475702ef964e95f8a885");
                Keccak properHash = new Keccak("0xe51a2d9f986d68628990c9d65e45c36128ec7bb697bd426b0bb4d18a3f3321be");
                yield return new TestCaseData(true, skipHash).Returns(properHash);
                yield return new TestCaseData(false, skipHash).Returns(skipHash);
                yield return new TestCaseData(false, Keccak.Zero).Returns(properHash);
            }
        }
        
        [TestCaseSource(nameof(ReceiptsRootTestCases))]
        public Keccak Should_Calculate_ReceiptsRoot(bool validateReceipts, Keccak suggestedRoot)
        {
            
            TxReceipt[] receipts = {Build.A.Receipt.WithAllFieldsFilled.TestObject};
            return receipts.GetReceiptsRoot(new ReleaseSpec() {ValidateReceipts = validateReceipts}, suggestedRoot);
        }
    }
}

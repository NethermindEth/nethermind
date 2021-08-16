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

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators
{
    [TestFixture]
    public class TxValidatorTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void Zero_r_is_not_valid()
        {
            byte[] sigData = new byte[65];
            // r is zero
            sigData[63] = 1; // correct s

            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
        }
        
        [Test]
        public void Zero_s_is_not_valid()
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            // s is zero
            
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
        }
        
        [Test]
        public void Bad_chain_id_is_not_valid()
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 39;
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
        }
        
        [Test]
        public void No_chain_id_tx_is_valid()
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeTrue();
        }
        
        [Test]
        public void Is_valid_with_valid_chain_id()
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 38;
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeTrue();
        }
        
        [TestCase(true)]
        [TestCase(false)]
        public void Before_eip_155_has_to_have_valid_chain_id_unless_overridden(bool validateChainId)
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 41;
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip155Enabled.Returns(false);
            releaseSpec.ValidateChainId.Returns(validateChainId);
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, releaseSpec).Should().Be(!validateChainId);
        }
        
        [TestCase(TxType.Legacy, true, ExpectedResult = true)]
        [TestCase(TxType.Legacy, false, ExpectedResult = true)]
        [TestCase(TxType.AccessList, false, ExpectedResult = false)]
        [TestCase(TxType.AccessList, true, ExpectedResult = true)]
        [TestCase((TxType)100, true, ExpectedResult = false)]
        public bool Before_eip_2930_has_to_be_legacy_tx(TxType txType, bool eip2930)
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 38;
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction
                .WithType(txType > TxType.AccessList ? TxType.Legacy : txType)
                .WithChainId(ChainId.Mainnet)
                .WithAccessList(txType == TxType.AccessList ? new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()) : null)
                .WithSignature(signature).TestObject;

            tx.Type = txType;
            
            TxValidator txValidator = new TxValidator(1);
            return txValidator.IsWellFormed(tx, eip2930 ? Berlin.Instance : MuirGlacier.Instance);
        }
        
        [TestCase(TxType.Legacy, true, false, ExpectedResult = true)]
        [TestCase(TxType.Legacy, false, false, ExpectedResult = true)]
        [TestCase(TxType.AccessList, false, false, ExpectedResult = false)]
        [TestCase(TxType.AccessList, true, false, ExpectedResult = true)]
        [TestCase(TxType.EIP1559, true, false, ExpectedResult = false)]
        [TestCase(TxType.EIP1559, true, true, ExpectedResult = true)]
        [TestCase((TxType)100, true, false, ExpectedResult = false)]
        public bool Before_eip_1559_has_to_be_legacy_or_access_list_tx(TxType txType, bool eip2930, bool eip1559)
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 38;
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction
                .WithType(txType)
                .WithChainId(ChainId.Mainnet)
                .WithMaxPriorityFeePerGas(txType == TxType.EIP1559 ? 10.GWei() : 5.GWei())
                .WithMaxFeePerGas(txType == TxType.EIP1559 ? 10.GWei() : 5.GWei())
                .WithAccessList(txType == TxType.AccessList || txType == TxType.EIP1559 ? new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()) : null)
                .WithSignature(signature).TestObject;

            tx.Type = txType;
            
            TxValidator txValidator = new TxValidator(1);
            IReleaseSpec releaseSpec = new ReleaseSpec() {IsEip2930Enabled = eip2930, IsEip1559Enabled = eip1559};
            return txValidator.IsWellFormed(tx, releaseSpec);
        }
        
        
        [TestCase(TxType.Legacy, ExpectedResult = true)]
        [TestCase(TxType.AccessList, ExpectedResult = false)]
        [TestCase(TxType.EIP1559, ExpectedResult = false)]
        public bool Chain_Id_required_for_non_legacy_transactions_after_Berlin(TxType txType)
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 38;
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction
                .WithType(txType > TxType.AccessList ? TxType.Legacy : txType)
                .WithAccessList(txType == TxType.AccessList ? new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()) : null)
                .WithSignature(signature).TestObject;

            tx.Type = txType;
            
            TxValidator txValidator = new TxValidator(ChainId.Mainnet);
            return txValidator.IsWellFormed(tx, Berlin.Instance);
        }

        [TestCase(TxType.Legacy, 10, 5, ExpectedResult = true)]
        [TestCase(TxType.AccessList, 10, 5, ExpectedResult = true)]
        [TestCase(TxType.EIP1559, 10, 5, ExpectedResult = true)]
        [TestCase(TxType.Legacy, 5, 10, ExpectedResult = true)]
        [TestCase(TxType.AccessList, 5, 10, ExpectedResult = true)]
        [TestCase(TxType.EIP1559, 5, 10, ExpectedResult = false)]
        public bool MaxFeePerGas_is_required_to_be_greater_than_MaxPriorityFeePerGas(TxType txType, int maxFeePerGas, int maxPriorityFeePerGas)
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 38;
            Signature signature = new Signature(sigData);
            Transaction tx = Build.A.Transaction
                .WithType(txType > TxType.AccessList ? TxType.Legacy : txType)
                .WithMaxPriorityFeePerGas((UInt256)maxPriorityFeePerGas)
                .WithMaxFeePerGas((UInt256)maxFeePerGas)
                .WithAccessList(txType == TxType.AccessList ? new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()) : null)
                .WithChainId(ChainId.Mainnet)
                .WithSignature(signature).TestObject;

            tx.Type = txType;
            
            TxValidator txValidator = new TxValidator(ChainId.Mainnet);
            return txValidator.IsWellFormed(tx, London.Instance);
        }
    }
}

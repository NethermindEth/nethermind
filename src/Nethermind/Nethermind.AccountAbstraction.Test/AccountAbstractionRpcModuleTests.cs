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
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public partial class AccountAbstractionRpcModuleTests
    {
        public static class Contracts
        {
            public static string SingletonCode = "";
            public static string SimpleWalletCode = "";
            public static string SimplePaymasterCode = "";
            
            public static long LargeGasLimit = 2_000_000;
            
            private static PrivateKey ContractCreatorPrivateKey = TestItem.PrivateKeyC;

            public static async Task<(Address, Address?, Address?)> Deploy(TestAccountAbstractionRpcBlockchain chain, string? walletCode = null, string? paymasterCode = null)
            {
                bool createWallet = walletCode is not null;
                bool createPaymaster = paymasterCode is not null;
                
                
                IList<Transaction> transactionsToInclude = new List<Transaction>();

                Transaction singletonTx = Build.A.Transaction.WithCode(Bytes.FromHexString(SingletonCode)).WithGasLimit(LargeGasLimit).WithNonce(0).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                transactionsToInclude.Add(singletonTx);
                
                Transaction? walletTx = createWallet ? Build.A.Transaction.WithCode(Bytes.FromHexString(walletCode)).WithGasLimit(LargeGasLimit).WithNonce(1).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject : null;
                if (createWallet) transactionsToInclude.Add(walletTx!);
                
                Transaction? paymasterTx = createPaymaster ? Build.A.Transaction.WithCode(Bytes.FromHexString(paymasterCode)).WithGasLimit(LargeGasLimit).WithNonce(2).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject : null;
                if (createPaymaster) transactionsToInclude.Add(paymasterTx!);
                
                await chain.AddBlock(true, transactionsToInclude.ToArray());

                TxReceipt createSingletonTxReceipt = chain.Bridge.GetReceipt(singletonTx.Hash!);
                TxReceipt? createWalletTxReceipt = createWallet ? chain.Bridge.GetReceipt(walletTx.Hash!) : null;
                TxReceipt? createPaymasterTxReceipt = createPaymaster ? chain.Bridge.GetReceipt(paymasterTx.Hash!) : null;
                createSingletonTxReceipt.ContractAddress.Should().NotBeNull($"Contract transaction {singletonTx.Hash!} was not deployed.");
                createWalletTxReceipt?.ContractAddress.Should().NotBeNull($"Contract transaction {walletTx?.Hash!} was not deployed.");
                createPaymasterTxReceipt?.ContractAddress.Should().NotBeNull($"Contract transaction {paymasterTx?.Hash!} was not deployed.");
                
                return (createSingletonTxReceipt.ContractAddress!, createWalletTxReceipt?.ContractAddress!, createPaymasterTxReceipt?.ContractAddress!);
            }
        }

        [Test]
        public async Task Should_deploy_contracts_successfully()
        {
            var chain = await CreateChain();
            (Address singletonAddress, Address? walletAddress, Address? paymasterAddress) = await Contracts.Deploy(chain, Contracts.SimpleWalletCode, Contracts.SimplePaymasterCode);
        }
        
        [Test]
        public async Task Should_execute_well_formed_op_successfully()
        {
            var chain = await CreateChain();
            (Address singletonAddress, Address? walletAddress, Address? paymasterAddress) = await Contracts.Deploy(chain, Contracts.SimpleWalletCode, Contracts.SimplePaymasterCode);
            
            
        }
    }
}

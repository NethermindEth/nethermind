/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Cli.Modules
{
    [CliModule("ndm")]
    public class NdmCliModule : CliModuleBase
    {
        [CliFunction("ndm", "deploy")]
        public string Deploy(string address)
        {
            TransactionForRpc tx = new TransactionForRpc();
            tx.Value = 0;
            tx.Data = Bytes.FromHexString(ContractData.GetInitCode(new Address(address)));
            tx.Gas = 3000000;
            tx.GasPrice = (UInt256) (Engine.JintEngine.GetValue("gasPrice").AsNumber());
            tx.From = new Address(address);

            Keccak keccak = NodeManager.Post<Keccak>("eth_sendTransaction", tx).Result;
            ReceiptForRpc receipt = null;
            while (receipt == null)
            {
                Console.WriteLine("Awaiting receipt...");
                receipt = NodeManager.Post<ReceiptForRpc>("eth_getTransactionReceipt", keccak).Result;
                Thread.Sleep(1000);
            }

            return receipt.ContractAddress.ToString();
        }
        
        [CliFunction("ndm", "recognize")]
        public string Recognize(string data)
        {
            if (data.StartsWith(ContractData.DepositAbiSig.Address.ToHexString()))
            {
                return "Deposit";
            }
            
            if (data.StartsWith(ContractData.ClaimPaymentAbiSig.Address.ToHexString()))
            {
                return "ClaimPayment";
            }
            
            if (data.StartsWith(ContractData.ClaimRefundSig.Address.ToHexString()))
            {
                return "ClaimRefund";
            }
            
            if (data.StartsWith(ContractData.ClaimEarlyRefundSig.Address.ToHexString()))
            {
                return "ClaimEarlyRefund";
            }
            
            if (data.StartsWith(ContractData.VerifyDepositAbiSig.Address.ToHexString()))
            {
                return "VerifyDeposit";
            }
            
            if (data.StartsWith(ContractData.DepositBalanceAbiSig.Address.ToHexString()))
            {
                return "DepositBalance";
            }

            return data;
        }
        
        public NdmCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
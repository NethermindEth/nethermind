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

using System.Threading;
using Jint.Native;
using Nethermind.Cli;
using Nethermind.Cli.Modules;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.DataMarketplace.CLi
{
    [CliModule("ndm")]
    public class NdmCliModule : CliModuleBase
    {
        [CliFunction("ndm", "deploy")]
        public JsValue Deploy(string address)
        {
            TransactionForRpc tx = new TransactionForRpc();
            tx.Value = 0;
            tx.Data = Bytes.FromHexString(ContractData.GetInitCode(new Address(address)));
            tx.Gas = 2000000;
            tx.GasPrice = (UInt256) (Engine.JintEngine.GetValue("gasPrice").AsNumber());
            tx.From = new Address(address);

            Keccak txHash = NodeManager.Post<Keccak>("eth_sendTransaction", tx).Result;
            Colorful.Console.WriteLine($"Sent transaction {tx.From}->{tx.To} with gas {tx.Gas} at price {tx.GasPrice} and received tx hash: {txHash}");
            if (txHash == null)
            {
                return null;
            }
            
            JsValue receipt = null;
            while (receipt == JsValue.Null)
            {
                Colorful.Console.WriteLine("Awaiting receipt...");
                receipt = NodeManager.PostJint("eth_getTransactionReceipt", txHash).Result;
                Thread.Sleep(1000);
            }

            return receipt;
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
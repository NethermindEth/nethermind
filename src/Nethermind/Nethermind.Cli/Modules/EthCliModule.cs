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

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Cli.Modules
{
    [CliModule]
    public class EthCliModule : CliModuleBase
    {
        private string SendEth(Address from, Address address, decimal amount)
        {
            UInt256 blockNumber = NodeManager.Post<UInt256>("eth_blockNumber").Result;

            TransactionForRpc tx = new TransactionForRpc();
            tx.Value = (UInt256) (amount * (decimal) 1.Ether());
            tx.Gas = 21000;
            tx.GasPrice = (UInt256) (Engine.JintEngine.GetValue("gasPrice").AsNumber());
            tx.To = address;
            tx.Nonce = (ulong) NodeManager.Post<BigInteger>("eth_getTransactionCount", address, blockNumber).Result;
            tx.From = from;

            Keccak keccak = NodeManager.Post<Keccak>("eth_sendTransaction", tx).Result;
            return keccak.Bytes.ToHexString();
        }
        
        [CliFunction("eth", "sendEth")]
        public string ListAccounts(string from, string to, decimal amount)
        {
            return SendEth(new Address(from), new Address(to), amount);
        }
        
        [CliProperty("eth", "blockNumber")]
        public BigInteger BlockNumber()
        {
            return NodeManager.Post<BigInteger>("eth_blockNumber").Result;
        }
        
        [CliFunction("eth", "getCode")]
        public string GetCode(string address, string blockParameter)
        {
            return NodeManager.Post<string>("eth_getCode", address, blockParameter).Result;
        }
        
        [CliFunction("eth", "getBalance")]
        public string GetBalance(string address, string blockParameter)
        {
            return NodeManager.Post<string>("eth_getBalance", address, blockParameter).Result;
        }
        
        [CliProperty("eth", "protocolVersion")]
        public int ProtocolVersion()
        {
            return NodeManager.Post<int>("eth_protocolVersion").Result;
        }

        public EthCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
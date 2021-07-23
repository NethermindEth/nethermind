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
using Nethermind.Core.Attributes;

namespace Nethermind.JsonRpc.Modules
{
    public static class ModuleType
    {
        public const string Admin = nameof(Admin);
        public const string Clique = nameof(Clique);
        public const string Consensus = nameof(Consensus);
        public const string Db = nameof(Db);
        public const string Debug = nameof(Debug);
        public const string Erc20 = nameof(Erc20);
        public const string Eth = nameof(Eth);
        public const string Evm = nameof(Evm);
        public const string Mev = nameof(Mev);
        public const string NdmProvider = nameof(NdmProvider);
        public const string NdmConsumer = nameof(NdmConsumer);
        public const string Net = nameof(Net);
        public const string Nft = nameof(Nft);
        public const string Parity = nameof(Parity);
        public const string Personal = nameof(Personal);
        public const string Proof = nameof(Proof);
        public const string Subscribe = nameof(Subscribe);
        public const string Trace = nameof(Trace);
        public const string TxPool = nameof(TxPool);
        public const string Web3 = nameof(Web3);
        public const string Baseline = nameof(Baseline);
        public const string Vault = nameof(Vault);
        public const string Deposit = nameof(Deposit);
        public const string Health= nameof(Health);
        public const string Witness = nameof(Witness);
        
        public static IEnumerable<string> AllBuiltInModules { get; } = new List<string>()
        {
            Admin,
            Clique,
            Consensus,
            Db,
            Debug,
            Erc20,
            Eth,
            Evm,
            Mev,
            NdmProvider,
            NdmConsumer,
            Net,
            Nft,
            Parity,
            Personal,
            Proof,
            Subscribe,
            Trace,
            TxPool,
            Web3,
            Baseline,
            Vault,
            Deposit,
            Health,
            Witness
        };

        public static IEnumerable<string> DefaultModules { get; } = new List<string>()
        {
            Eth, 
            Subscribe, 
            Trace, 
            TxPool, 
            Web3, 
            Personal, 
            Proof, 
            Net,
            Parity,
            Health
        };
    }
}

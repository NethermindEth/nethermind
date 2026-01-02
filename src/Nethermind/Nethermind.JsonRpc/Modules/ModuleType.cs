// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.JsonRpc.Modules
{
    public static class ModuleType
    {
        public const string Admin = nameof(Admin);
        public const string Clique = nameof(Clique);
        public const string Engine = nameof(Engine);
        public const string Db = nameof(Db);
        public const string Debug = nameof(Debug);
        public const string Erc20 = nameof(Erc20);
        public const string Eth = nameof(Eth);
        public const string Evm = nameof(Evm);
        public const string Flashbots = nameof(Flashbots);
        public const string Net = nameof(Net);
        public const string Nft = nameof(Nft);
        public const string Parity = nameof(Parity);
        public const string Personal = nameof(Personal);
        public const string Proof = nameof(Proof);
        public const string Subscribe = nameof(Subscribe);
        public const string Trace = nameof(Trace);
        public const string TxPool = nameof(TxPool);
        public const string Web3 = nameof(Web3);
        public const string Vault = nameof(Vault);
        public const string Deposit = nameof(Deposit);
        public const string Health = nameof(Health);
        public const string Rpc = nameof(Rpc);
        public const string Rbuilder = nameof(Rbuilder);

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
            Health,
            Rpc,
        };

        public static IEnumerable<string> DefaultEngineModules { get; } = new List<string>()
        {
            Net,
            Eth,
            Subscribe,
            Web3
        };
    }
}

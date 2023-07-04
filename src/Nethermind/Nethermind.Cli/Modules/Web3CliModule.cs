// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint.Native;
using Nethermind.Abi;
using Nethermind.Core.Extensions;

namespace Nethermind.Cli.Modules
{
    [CliModule("web3")]
    public class Web3CliModule : CliModuleBase
    {
        public Web3CliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }

        [CliProperty("web3", "clientVersion")]
        public string? ClientVersion() => NodeManager.Post<string>("web3_clientVersion").Result;

        [CliFunction("web3", "sha3")]
        public string? Sha3(string data) => NodeManager.Post<string>("web3_sha3", data).Result;

        [CliFunction("web3", "toDecimal")]
        public JsValue ToDecimal(string hex) => Engine.Execute(hex);

        [CliFunction("web3", "abi")]
        public string Abi(string name) => new AbiSignature(name).Address.ToHexString();
    }
}

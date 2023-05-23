// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint.Native;
using Nethermind.Core;

namespace Nethermind.Cli.Modules
{
    [CliModule("parity")]
    public class ParityCliModule : CliModuleBase
    {
        public ParityCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }

        [CliFunction("parity", "pendingTransactions", Description = "Returns the pending transactions using Parity format")]
        public JsValue PendingTransactions() => NodeManager.PostJint("parity_pendingTransactions").Result;

        [CliFunction("parity", "getBlockReceipts", Description = "Returns receipts from all transactions from particular block")]
        public JsValue GetBlockReceipts(string blockParameter) => NodeManager.PostJint("parity_getBlockReceipts", blockParameter).Result;

        [CliProperty("parity", "enode", Description = "Returns the node enode URI.")]
        public string? Enode() => NodeManager.Post<string>("parity_enode").Result;

        [CliFunction("parity", "clearEngineSigner", Description = "Clears an authority account for signing consensus messages. Blocks will not be sealed.")]
        public bool ClearSigner() => NodeManager.Post<bool>("parity_clearEngineSigner").Result;

        [CliFunction("parity", "setEngineSigner", Description = "Sets an authority account for signing consensus messages.")]
        public bool SetSigner(Address address, string password) => NodeManager.Post<bool>("parity_setEngineSigner", address, password).Result;

        [CliFunction("parity", "setEngineSignerSecret", Description = "Sets an authority account for signing consensus messages.")]
        public bool SetEngineSignerSecret(string privateKey) => NodeManager.Post<bool>("parity_setEngineSignerSecret", privateKey).Result;

        [CliProperty("parity", "netPeers", Description = "Returns connected peers. Peers with non-empty protocols have completed handshake.")]
        public JsValue NetPeers() => NodeManager.PostJint("parity_netPeers").Result;
    }
}

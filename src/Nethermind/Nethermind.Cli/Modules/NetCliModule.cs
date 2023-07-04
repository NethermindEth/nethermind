// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint.Native;

namespace Nethermind.Cli.Modules
{
    [CliModule("net")]
    public class NetCliModule : CliModuleBase
    {
        [CliProperty("net", "localEnode")]
        public string? LocalEnode() => NodeManager.Post<string>("net_localEnode").Result;

        [CliProperty("net", "version")]
        public JsValue Version() => NodeManager.PostJint("net_version").Result;

        [CliProperty("net", "peerCount")]
        public long PeerCount() => NodeManager.Post<long>("net_peerCount").Result;

        public NetCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Cli.Modules
{
    [CliModule("admin")]
    public class AdminCliModule : CliModuleBase
    {
        public AdminCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }

        [CliFunction("admin", "peers", Description = "Displays a list of connected peers")]
        public object[]? Peers(bool includeDetails = false) => NodeManager.Post<object[]>("admin_peers", includeDetails).Result;

        [CliProperty("admin", "nodeInfo")]
        public object? NodeInfo() => NodeManager.Post<object>("admin_nodeInfo").Result;

        [CliFunction("admin", "addPeer", Description = "Adds given node to the static nodes")]
        public string? AddPeer(string enode, bool addToStaticNodes = false) => NodeManager.Post<string>("admin_addPeer", enode, addToStaticNodes).Result;

        [CliFunction("admin", "removePeer", Description = "Removes given node from the static nodes")]
        public string? RemovePeer(string enode, bool removeFromStaticNodes = false) => NodeManager.Post<string>("admin_removePeer", enode, removeFromStaticNodes).Result;
    }
}

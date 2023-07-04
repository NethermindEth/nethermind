// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Cli;
using Nethermind.Cli.Modules;

namespace Nethermind.Analytics
{
    [CliModule("analytics")]
    public class AnalyticsCliModule : CliModuleBase
    {
        [CliFunction("analytics", "verifySupply")]
        public string VerifySupply()
        {
            return NodeManager.Post<string>("analytics_verifySupply").Result;
        }

        [CliFunction("analytics", "verifyRewards")]
        public string VerifyRewards()
        {
            return NodeManager.Post<string>("analytics_verifyRewards").Result;
        }

        public AnalyticsCliModule(ICliEngine cliEngine, INodeManager nodeManager)
            : base(cliEngine, nodeManager) { }
    }
}

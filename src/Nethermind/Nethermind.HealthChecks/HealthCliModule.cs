// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// using Nethermind.Cli;
// using Nethermind.Cli.Modules;

// namespace Nethermind.HealthChecks
// {
//     [CliModule("health")]
//     public class HealthCliModule : CliModuleBase
//     {
//         public HealthCliModule(ICliEngine cliEngine, INodeManager nodeManager)
//             : base(cliEngine, nodeManager)
//         {
//         }

//         [CliFunction("health", "nodeStatus")]
//         public NodeStatusResult NodeStatus()
//         {
//             return NodeManager.Post<NodeStatusResult>("health_nodeStatus").Result;
//         }
//     }
// }

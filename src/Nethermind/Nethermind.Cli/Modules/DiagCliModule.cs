// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint.Native;

namespace Nethermind.Cli.Modules
{
    [CliModule("diag")]
    public class DiagCliModule : CliModuleBase
    {
        [CliProperty("diag", "cliVersion",
            Description = "Displays client version",
            ResponseDescription = "Client version",
            ExampleResponse = "\"Nethermind.Cli, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\"")]
        public JsValue CliVersion()
        {
            return GetType().Assembly.FullName;
        }

        public DiagCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}

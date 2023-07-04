// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint.Native;

namespace Nethermind.Cli.Modules
{
    [CliModule("txpool")]
    public class TxPoolCliModule : CliModuleBase
    {
        public TxPoolCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }

        [CliProperty("txpool", "status")]
        public JsValue Status()
        {
            return NodeManager.PostJint("txpool_status").Result;
        }

        [CliProperty("txpool", "content")]
        public JsValue Content()
        {
            return NodeManager.PostJint("txpool_content").Result;
        }

        [CliProperty("txpool", "inspect")]
        public JsValue Inspect()
        {
            return NodeManager.PostJint("txpool_inspect").Result;
        }
    }
}

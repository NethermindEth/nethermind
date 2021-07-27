using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;

namespace TestPlugin
{
    public class PluginClassName : INethermindPlugin
    {
        public string Name => "MyTestPlugin";

        public string Description => "My TestPlugin";

        public string Author => "nethermind";

        private IBlockTree _blockTree;
        private string _filePath = "plugin-logs.txt"; // this is currently hard coded but there is nothing standing in our way to add config file with this path in it

        public Task Init(INethermindApi nethermindApi)
        {
            // Once you code your plugin delete the following line
            nethermindApi.LogManager.GetClassLogger().Warn($"Plugin {Name} created but not modified");
            _blockTree = nethermindApi.BlockTree;
            _blockTree.NewHeadBlock += WriteTransactionsToFile; //on every new head block run our method
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;


        private void WriteTransactionsToFile(object sender, BlockEventArgs args)
        {
            using (var writer = new StreamWriter(_filePath, true)) // StreamWritter will check if file exists - if not - will create it
            {
                writer.WriteLine($"BLOCK #{args.Block.Number}");
                foreach(Transaction transaction in args.Block.Transactions)
                {
                    writer.WriteLine(transaction.ToString()); //we got cool ToString method in transaction class wich will give us all information we need
                }
            }
        }
    }
}
using System;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Runner.Runners;

namespace Nethermind.Runner
{
    public class HiveRunnerApp : BaseRunnerApp, IRunnerApp
    {
        private static readonly PrivateKey PrivateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");

        private string _host = "0.0.0.0";
        private string _bootNode = "enode://6ce05930c72abc632c58e2e4324f7c7ea478cec0ed4fa2528982cf34483094e9cbc9216e7aa349691242576d552a2a56aaeae426c5303ded677ce455ba1acd9d@13.84.180.240:30303";
        private int _httpPort = 8545;
        private int _discoveryPort = 30303;
        private string _genesisFile = "genesis.json";
        private string _chainFile = "chain.rlp";
        private string _blocksDir = "blocks";
        private string _keysDir = "keys";

        public HiveRunnerApp(ILogger logger) : base(logger, new PrivateKeyProvider(PrivateKey))
        {
        }

        public void Start(string[] args)
        {
            var app = new CommandLineApplication { Name = "Hive Nethermind.Runner" };
            app.HelpOption("-?|-h|--help");
            
            var host = app.Option("-ho|--host <host>", "server host", CommandOptionType.SingleValue);
            var bootNode = app.Option("-b|--bootNode <bootNode>", "enode URL of the remote bootstrap node", CommandOptionType.SingleValue);
            var httpPort = app.Option("-p|--httpPort <httpPort>", "JsonRPC http listening port", CommandOptionType.SingleValue);
            var discoveryPort = app.Option("-d|--discoveryPort <discoveryPort>", "discovery UDP listening port", CommandOptionType.SingleValue);
            var genesisFile = app.Option("-gf|--genesisFile <genesisFile>", "genesis file path", CommandOptionType.SingleValue);
            var chainFile = app.Option("-cf|--chainFile <chainFile>", "chain file path", CommandOptionType.SingleValue);
            var blocksDir = app.Option("-bd|--blocksDir <blocksDir>", "blocks directory path", CommandOptionType.SingleValue);
            var keysDir = app.Option("-kd|--keysDir <keysDir>", "keys directory path", CommandOptionType.SingleValue);
            var homesteadBlockNr = app.Option("-fh|--homesteadBlockNr <homesteadBlockNr>", "the block number of the Ethereum Homestead transition", CommandOptionType.SingleValue);

            app.OnExecute(() => {
                
                var initParams = new InitParams
                {
                    HttpHost = host.HasValue() ? host.Value() : _host,
                    BootNode = bootNode.HasValue() ? bootNode.Value() : _bootNode,
                    HttpPort = httpPort.HasValue() ? GetIntValue(httpPort.Value(), "httpPort") : _httpPort,
                    DiscoveryPort = discoveryPort.HasValue() ? GetIntValue(discoveryPort.Value(), "discoveryPort") : _discoveryPort,
                    GenesisFilePath = genesisFile.HasValue() ? genesisFile.Value() : _genesisFile,
                    ChainFile = chainFile.HasValue() ? chainFile.Value() : _chainFile,
                    BlocksDir = blocksDir.HasValue() ? blocksDir.Value() : _blocksDir,
                    KeysDir = keysDir.HasValue() ? keysDir.Value() : _keysDir,
                    HomesteadBlockNr = homesteadBlockNr.HasValue() ? GetBigIntValue(homesteadBlockNr.Value(), "homesteadBlockNr") : (BigInteger?)null,
                    EthereumRunnerType = EthereumRunnerType.Hive
                };

                Logger.Log($"Running Hive Nethermind Runner, parameters: {initParams}");

                Run(initParams);

                while (true)
                {
                    Console.WriteLine("Enter e to exit");
                    var value = Console.ReadLine();
                    if ("e".CompareIgnoreCaseTrim(value))
                    {
                        Logger.Log("Closing app");
                        break;
                    }
                    Thread.Sleep(2000);
                }

                return 0;
            });

            app.Execute(args);
        }
    }
}
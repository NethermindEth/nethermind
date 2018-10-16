/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Runner.Runners;

namespace Nethermind.Runner
{
    public class HiveRunnerApp : RunnerAppBase, IRunnerApp
    {
        private const string DefaultHost = "localhost";
        private const string DefaultBootNode = "enode://6ce05930c72abc632c58e2e4324f7c7ea478cec0ed4fa2528982cf34483094e9cbc9216e7aa349691242576d552a2a56aaeae426c5303ded677ce455ba1acd9d@13.84.180.240:30303";
        private const int DefaultHttpPort = 8345;
        private const int DefaultDiscoveryPort = 30303;
        private readonly string _defaultGenesisFile = Path.Combine("Data", "genesis.json");
        private const string DefaultChainFile = "chain.rlp";
        private const string DefaultBlocksDir = "blocks";
        private const string DefaultKeysDir = "keys";

        public HiveRunnerApp(ILogger logger) : base(logger)
        {
        }

        protected override (CommandLineApplication, Func<IConfigProvider>, Func<string>) BuildCommandLineApp()
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

            //InitParams InitParams() => new InitParams
            //{
            //    HttpHost = host.HasValue() ? host.Value() : DefaultHost,
            //    Bootnode = bootNode.HasValue() ? bootNode.Value() : DefaultBootNode,
            //    HttpPort = httpPort.HasValue() ? GetIntValue(httpPort.Value(), "httpPort") : DefaultHttpPort,
            //    DiscoveryPort = discoveryPort.HasValue() ? GetIntValue(discoveryPort.Value(), "discoveryPort") : DefaultDiscoveryPort,
            //    GenesisFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, genesisFile.HasValue() ? genesisFile.Value() : _defaultGenesisFile),
            //    ChainFile = chainFile.HasValue() ? chainFile.Value() : DefaultChainFile,
            //    BlocksDir = blocksDir.HasValue() ? blocksDir.Value() : DefaultBlocksDir,
            //    KeysDir = keysDir.HasValue() ? keysDir.Value() : DefaultKeysDir,
            //    HomesteadBlockNr = homesteadBlockNr.HasValue() ? GetBigIntValue(homesteadBlockNr.Value(), "homesteadBlockNr") : (BigInteger?)null,
            //    EthereumRunnerType = EthereumRunnerType.Hive
            //};

            return (app, null, null);
        }
    }
}
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
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Jint;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Cli
{   
    static class Program
    {
        private static IJsonSerializer _serializer = new EthereumJsonSerializer();
        private static JsonRpcClientWrapper _client;
        private static ILogManager _logManager;
        private static Engine _engine;
        private static PrivateKey _nodeKey;

        // ReSharper disable once InconsistentNaming
        private static CliApiBuilder Build;

        static void Main(string[] args)
        {
            string privateKeyPath = args.Length > 0 ? args[0] : "node.key.plain";

            _logManager = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Debug));
            _client = new JsonRpcClientWrapper(_serializer, _logManager);
            _client.SwitchUri(new Uri("http://localhost:8545"));

            _engine = new Engine();
            _engine.SetValue("gasPrice", (double) 20.GWei());

            Build = new CliApiBuilder(_engine, _serializer, _client, _logManager);

            BuildPersonal();
            BuildEth();
            BuildNet();
            BuildWeb3();
            BuildClique();

            if (File.Exists(privateKeyPath))
            {
                BuildNode();
                _nodeKey = new PrivateKey(File.ReadAllBytes(privateKeyPath));
            }

            while (true)
            {
                try
                {
                    Console.Write("> ");
                    var statement = Console.ReadLine();
                    if (statement == "exit")
                    {
                        break;
                    }

                    Console.WriteLine(_engine.Execute(statement).GetCompletionValue());
                }
                catch (Exception e)
                {
                    var color = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(e);
                    Console.ForegroundColor = color;
                }
            }
        }

        private static void BuildPersonal()
        {
            Build.Api("personal")
                .WithProperty<string[]>("listAccounts")
                .WithFunc<string, string>("newAccount")
                .WithFunc<string, bool>("lockAccount")
                .WithFunc<string, bool>("unlockAccount")
                .Build();
        }

        private static void BuildEth()
        {
            Build.Api("eth")
                .WithProperty<BigInteger>("blockNumber")
                .WithProperty<int>("protocolVersion")
                .WithFunc<string, string, string>("getCode")
                .WithFunc<string, string, BigInteger>("getBalance")
                .WithFunc("sendEth", (string from, string to, decimal amount) => SendEth(new Address(from), new Address(to), amount))
                .Build();
        }

        private static void BuildWeb3()
        {
            Build.Api("web3")
                .WithProperty<string>("clientVersion")
                .WithFunc<string, string>("sha3")
                .Build();
        }

        private static void BuildNet()
        {
            Build.Api("net")
                .WithProperty<BigInteger>("version")
                .WithProperty<BigInteger>("peerCount")
                .Build();
        }

        private static void BuildClique()
        {
            Build.Api("clique")
                .WithFunc<string[]>("getSnapshot")
                .WithFunc<string, string[]>("getSnapshotAtHash")
                .WithFunc<string[]>("getSigners")
                .WithFunc<string, string[]>("getSignersAtHash")
                .WithFunc<string, bool, bool>("propose")
                .WithFunc<string, bool>("discard")
                .Build();
        }

        private static void BuildNode()
        {
            Console.WriteLine("Enabling node operations");
            Build.Api("node")
                .WithProperty("uri", () => _client.CurrentUri)
                .WithAction("switch", (string uri) => _client.SwitchUri(new Uri(uri)))
                .WithAction("switchLocal", (string uri) => _client.SwitchUri(new Uri($"http://localhost:{uri}")))
                .WithFunc("sendEth", (string address, decimal amount) => SendEth(new Address(address), amount))
                .WithProperty<string>("address", () => _nodeKey.Address.ToString())
                .Build();
        }

        private static string SendEth(Address from, Address address, decimal amount)
        {
            UInt256 blockNumber = _client.Post<UInt256>("eth_blockNumber").Result;

            TransactionForRpc tx = new TransactionForRpc();
            tx.Value = (UInt256) (amount * (decimal) 1.Ether());
            tx.Gas = 21000;
            tx.GasPrice = (UInt256) (_engine.GetValue("gasPrice").AsNumber());
            tx.To = address;
            tx.Nonce = (ulong) _client.Post<BigInteger>("eth_getTransactionCount", _nodeKey.Address, blockNumber).Result;
            tx.From = from;

            Keccak keccak = _client.Post<Keccak>("eth_sendTransaction", tx).Result;
            return _serializer.Serialize(keccak, true);
        }

        private static string SendEth(Address address, decimal amount)
        {
            UInt256 blockNumber = _client.Post<UInt256>("eth_blockNumber").Result;

            Transaction tx = new Transaction();
            tx.Value = (UInt256) (amount * (decimal) 1.Ether());
            tx.To = address;
            tx.GasLimit = 21000;
            tx.GasPrice = (UInt256) (_engine.GetValue("gasPrice").AsNumber());
            tx.Nonce = (ulong) _client.Post<BigInteger>("eth_getTransactionCount", _nodeKey.Address, blockNumber).Result;
            tx.SenderAddress = _nodeKey.Address;

            int chainId = _client.Post<int>("net_version").Result;
            EthereumSigner signer = new EthereumSigner(new SingleReleaseSpecProvider(ConstantinopleFix.Instance, chainId), _logManager);
            signer.Sign(_nodeKey, tx, blockNumber);

            Keccak keccak = _client.Post<Keccak>("eth_sendRawTransaction", Rlp.Encode(tx, false).Bytes).Result;
            return _serializer.Serialize(keccak, true);
        }
    }
}
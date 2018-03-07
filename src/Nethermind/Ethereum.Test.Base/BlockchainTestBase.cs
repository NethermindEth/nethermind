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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Potocol;
using Nethermind.Evm;
using Nethermind.Mining;
using Nethermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.Test.Base
{
    public class BlockchainTestBase
    {
        private readonly IProtocolSpecificationProvider _protocolSpecificationProvider = new ProtocolSpecificationProvider();
        private IBlockhashProvider _blockhashProvider;
        private IMultiDb _multiDb;
        private IBlockStore _chain;
        private Dictionary<EthereumNetwork, IVirtualMachine> _virtualMachines;
        private Dictionary<EthereumNetwork, StateProvider> _stateProviders; // TODO: support transitions of protocol
        private Dictionary<EthereumNetwork, IStorageProvider> _storageProviders;
        private Dictionary<EthereumNetwork, IBlockValidator> _blockValidators;
        private ILogger _logger;
        private static readonly Ethash Ethash = new Ethash(); // temporarily keep reusing the same one as otherwise it would recreate cache for each test

        protected void Setup(ILogger logger)
        {
            _logger = logger;
            ILogger stateLogger = ShouldLog.State ? _logger : null;
            _multiDb = new MultiDb(stateLogger);                
            _chain = new BlockStore();
            
            _blockhashProvider = new BlockhashProvider(_chain);
            _virtualMachines = new Dictionary<EthereumNetwork, IVirtualMachine>();
            _stateProviders = new Dictionary<EthereumNetwork, StateProvider>();
            _storageProviders = new Dictionary<EthereumNetwork, IStorageProvider>();
            _blockValidators = new Dictionary<EthereumNetwork, IBlockValidator>();
            EthereumNetwork[] networks = {EthereumNetwork.Frontier, EthereumNetwork.Homestead, EthereumNetwork.Byzantium, EthereumNetwork.SpuriousDragon, EthereumNetwork.TangerineWhistle};
            foreach (EthereumNetwork ethereumNetwork in networks)
            {
                IEthereumRelease spec = _protocolSpecificationProvider.GetSpec(ethereumNetwork, 1);
                ISignatureValidator signatureValidator = new SignatureValidator(spec, ChainId.MainNet);
                ITransactionValidator transactionValidator = new TransactionValidator(spec, signatureValidator);
                IBlockHeaderValidator headerValidator = new BlockHeaderValidator(_chain, Ethash);
                IOmmersValidator ommersValidator = new OmmersValidator(_chain, headerValidator);
                IBlockValidator blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, stateLogger);

                _blockValidators[ethereumNetwork] = blockValidator;
                _stateProviders[ethereumNetwork] = new StateProvider(new StateTree(_multiDb.CreateDb()), spec, stateLogger);
                _storageProviders[ethereumNetwork] = new StorageProvider(_multiDb, _stateProviders[ethereumNetwork], stateLogger);
                _virtualMachines[ethereumNetwork] = new VirtualMachine(
                    spec,
                    _stateProviders[ethereumNetwork],
                    _storageProviders[ethereumNetwork],
                    _blockhashProvider,
                    ShouldLog.Evm ? logger : null);
            }
        }

        [SetUp]
        public void Setup()
        {
            Setup(new ConsoleLogger());
        }

        public static IEnumerable<BlockchainTest> LoadTests(string testSet)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".", testSet);
            if (Directory.Exists(".\\Tests\\"))
            {
                testDirs = testDirs.Union(Directory.EnumerateDirectories(".\\Tests\\", testSet));
            }

            Dictionary<string, Dictionary<string, BlockchainTestJson>> testJsons = new Dictionary<string, Dictionary<string, BlockchainTestJson>>();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = LoadTestsFromDirectory(testDir);
            }

            return testJsons.SelectMany(d => d.Value).Select(pair => Convert(pair.Key, pair.Value));
        }

        private static Dictionary<string, BlockchainTestJson> LoadTestsFromDirectory(string testDir)
        {
            Dictionary<string, BlockchainTestJson> testsByName = new Dictionary<string, BlockchainTestJson>();
            List<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
            foreach (string testFile in testFiles)
            {
                string json = File.ReadAllText(testFile);
                Dictionary<string, BlockchainTestJson> testsInFile = JsonConvert.DeserializeObject<Dictionary<string, BlockchainTestJson>>(json);
                foreach (KeyValuePair<string, BlockchainTestJson> namedTest in testsInFile)
                {
                    if (namedTest.Key.EndsWith("Frontier"))
                    {
                        namedTest.Value.EthereumNetwork = EthereumNetwork.Frontier;
                    }
                    else if (namedTest.Key.EndsWith("Homestead"))
                    {
                        namedTest.Value.EthereumNetwork = EthereumNetwork.Homestead;
                    }
                    else if (namedTest.Key.EndsWith("EIP150"))
                    {
                        namedTest.Value.EthereumNetwork = EthereumNetwork.TangerineWhistle;
                    }
                    else if (namedTest.Key.EndsWith("EIP158"))
                    {
                        namedTest.Value.EthereumNetwork = EthereumNetwork.SpuriousDragon;
                    }
                    else if (namedTest.Key.EndsWith("Byzantium"))
                    {
                        namedTest.Value.EthereumNetwork = EthereumNetwork.Byzantium;
                    }
                    else
                    {
                        continue;
                    }

                    testsByName.Add(namedTest.Key, namedTest.Value);
                }
            }
            return testsByName;
        }

        private static AccountState Convert(AccountStateJson accountStateJson)
        {
            AccountState state = new AccountState();
            state.Balance = Hex.ToBytes(accountStateJson.Balance).ToUnsignedBigInteger();
            state.Code = Hex.ToBytes(accountStateJson.Code);
            state.Nonce = Hex.ToBytes(accountStateJson.Nonce).ToUnsignedBigInteger();
            state.Storage = accountStateJson.Storage.ToDictionary(
                p => Hex.ToBytes(p.Key).ToUnsignedBigInteger(),
                p => Hex.ToBytes(p.Value));
            return state;
        }

        private class LoggingTraceListener : TraceListener
        {
            private readonly ILogger _logger;

            public LoggingTraceListener(ILogger logger)
            {
                _logger = logger;
            }

            private readonly StringBuilder _line = new StringBuilder();

            public override void Write(string message)
            {
                _line.Append(message);
            }

            public override void WriteLine(string message)
            {
                Write(message);
                _logger?.Log(_line.ToString());
                _line.Clear();
            }
        }
        
        protected void RunTest(BlockchainTest test, Stopwatch stopwatch = null)
        {
            LoggingTraceListener traceListener = new LoggingTraceListener(_logger);
            // TODO: not supported in .NET Core, need to replace?
//            Debug.Listeners.Clear();
//            Debug.Listeners.Add(traceListener);

            InitializeTestState(test);

            // TODO: transition...
            _stateProviders[test.Network].EthereumRelease = _protocolSpecificationProvider.GetSpec(test.Network, 0);

            IEthereumRelease spec = _protocolSpecificationProvider.GetSpec(test.Network, 1);
            IEthereumSigner signer = new EthereumSigner(spec, ChainId.MainNet);
            IBlockProcessor blockProcessor = new BlockProcessor(
                spec,
                _chain,
                _blockValidators[test.Network],
                new ProtocolBasedDifficultyCalculator(spec),
                new RewardCalculator(spec),
                new TransactionProcessor(
                    spec,
                    _stateProviders[test.Network],
                    _storageProviders[test.Network],
                    _virtualMachines[test.Network],
                    signer,
                    ShouldLog.Processing ? _logger : null),
                _multiDb,
                _stateProviders[test.Network],
                _storageProviders[test.Network],
                new TransactionStore(),
                ShouldLog.Processing ? _logger : null);
            
            IBlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                test.GenesisRlp,
                blockProcessor,
                _chain,
                ShouldLog.Processing ? _logger : null);

            var rlps = test.Blocks.Select(tb => new Rlp(Hex.ToBytes(tb.Rlp))).ToArray();
            for (int i = 0; i < rlps.Length; i++)
            {
                stopwatch?.Start();
                try
                {
                    blockchainProcessor.Process(rlps[i]);
                }
                catch (InvalidBlockException ex)
                {
                }
                catch (Exception ex)
                {
                    _logger?.Log(ex.ToString());
                }
                
                stopwatch?.Stop();   
            }
            
            RunAssertions(test, blockchainProcessor.HeadBlock);
        }

        private void InitializeTestState(BlockchainTest test)
        {
            _stateProviders[test.Network].EthereumRelease = _protocolSpecificationProvider.GetSpec(EthereumNetwork.Frontier, 0);
            foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
            {
                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    _storageProviders[test.Network].Set(accountState.Key, storageItem.Key, storageItem.Value);
                }

                _stateProviders[test.Network].CreateAccount(accountState.Key, accountState.Value.Balance);
                Keccak codeHash = _stateProviders[test.Network].UpdateCode(accountState.Value.Code);
                _stateProviders[test.Network].UpdateCodeHash(accountState.Key, codeHash);
                for (int i = 0; i < accountState.Value.Nonce; i++)
                {
                    _stateProviders[test.Network].IncrementNonce(accountState.Key);
                }
            }

            _storageProviders[test.Network].Commit();
            _stateProviders[test.Network].Commit();
        }

        private void RunAssertions(BlockchainTest test, Block headBlock)
        {
            TestBlockHeaderJson testHeaderJson = test.Blocks
                                                     .Where(b => b.BlockHeader != null)
                                                     .SingleOrDefault(b => new Keccak(b.BlockHeader.Hash) == headBlock.Hash)?.BlockHeader ?? test.GenesisBlockHeader; 
            BlockHeader testHeader = Convert(testHeaderJson);
            List<string> differences = new List<string>();
            foreach (KeyValuePair<Address, AccountState> accountState in test.PostState)
            {
                if (differences.Count > 8)
                {
                    Console.WriteLine("More than 8 differences...");
                    break;
                }

                bool accountExists = _stateProviders[test.Network].AccountExists(accountState.Key);
                BigInteger? balance = accountExists ? _stateProviders[test.Network].GetBalance(accountState.Key) : (BigInteger?)null;
                BigInteger? nonce = accountExists ? _stateProviders[test.Network].GetNonce(accountState.Key) : (BigInteger?)null;

                if (accountState.Value.Balance != balance)
                {
                    differences.Add($"{accountState.Key} balance exp: {accountState.Value.Balance}, actual: {balance}, diff: {balance - accountState.Value.Balance}");
                }

                if (accountState.Value.Nonce != nonce)
                {
                    differences.Add($"{accountState.Key} nonce exp: {accountState.Value.Nonce}, actual: {nonce}");
                }

                byte[] code = accountExists ? _stateProviders[test.Network].GetCode(accountState.Key) : new byte[0];
                if (!Bytes.UnsafeCompare(accountState.Value.Code, code))
                {
                    differences.Add($"{accountState.Key} code exp: {accountState.Value.Code?.Length}, actual: {code?.Length}");
                }

                foreach (KeyValuePair<BigInteger, byte[]> storageItem in accountState.Value.Storage)
                {
                    byte[] value = _storageProviders[test.Network].Get(accountState.Key, storageItem.Key) ?? new byte[0];
                    if (!Bytes.UnsafeCompare(storageItem.Value, value))
                    {
                        differences.Add($"{accountState.Key} storage[{storageItem.Key}] exp: {Hex.FromBytes(storageItem.Value, true)}, actual: {Hex.FromBytes(value, true)}");
                    }
                }
            }


            BigInteger gasUsed = headBlock.Header.GasUsed;
            if ((testHeader?.GasUsed ?? 0) != gasUsed)
            {
                differences.Add($"GAS USED exp: {testHeader?.GasUsed ?? 0}, actual: {gasUsed}");
            }

            if (headBlock.Transactions.Any() && testHeader.Bloom.ToString() != headBlock.Receipts.Last().Bloom.ToString())
            {
                differences.Add($"BLOOM exp: {testHeader.Bloom}, actual: {headBlock.Receipts.Last().Bloom}");
            }

            if (testHeader.StateRoot != _stateProviders[test.Network].StateRoot)
            {
                differences.Add($"STATE ROOT exp: {testHeader.StateRoot}, actual: {_stateProviders[test.Network].StateRoot}");
            }

            if (testHeader.TransactionsRoot != headBlock.Header.TransactionsRoot)
            {
                differences.Add($"TRANSACTIONS ROOT exp: {testHeader.TransactionsRoot}, actual: {headBlock.Header.TransactionsRoot}");
            }

            if (testHeader.ReceiptsRoot!= headBlock.Header.ReceiptsRoot)
            {
                differences.Add($"RECEIPT ROOT exp: {testHeader.ReceiptsRoot}, actual: {headBlock.Header.ReceiptsRoot}");
            }

            foreach (string difference in differences)
            {
                _logger?.Log(difference);
            }

            Assert.Zero(differences.Count, "differences");
        }

        private static BlockHeader Convert(TestBlockHeaderJson headerJson)
        {
            if (headerJson == null)
            {
                return null;
            }

            BlockHeader header = new BlockHeader(
                new Keccak(headerJson.ParentHash),
                new Keccak(headerJson.UncleHash),
                new Address(headerJson.Coinbase),
                Hex.ToBytes(headerJson.Difficulty).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.Number).ToUnsignedBigInteger(),
                (long)Hex.ToBytes(headerJson.GasLimit).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.Timestamp).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.ExtraData)
            );

            header.Bloom = new Bloom(Hex.ToBytes(headerJson.Bloom).ToBigEndianBitArray2048());
            header.GasUsed = (long)Hex.ToBytes(headerJson.GasUsed).ToUnsignedBigInteger();
            header.Hash = new Keccak(headerJson.Hash);
            header.MixHash = new Keccak(headerJson.MixHash);
            header.Nonce = (ulong)Hex.ToBytes(headerJson.Nonce).ToUnsignedBigInteger();
            header.ReceiptsRoot = new Keccak(headerJson.ReceiptTrie);
            header.StateRoot = new Keccak(headerJson.StateRoot);
            header.TransactionsRoot = new Keccak(headerJson.TransactionsTrie);
            return header;
        }

        private static Block Convert(TestBlockJson testBlockJson)
        {
            BlockHeader header = Convert(testBlockJson.BlockHeader);
            BlockHeader[] ommers = testBlockJson.UncleHeaders?.Select(Convert).ToArray() ?? new BlockHeader[0];
            Block block = new Block(header, ommers);
            block.Transactions = testBlockJson.Transactions?.Select(Convert).ToList();
            return block;
        }

        private static Transaction Convert(TransactionJson transactionJson)
        {
            Transaction transaction = new Transaction();
            transaction.ChainId = ChainId.MainNet;
            transaction.Value = Hex.ToBytes(transactionJson.Value).ToUnsignedBigInteger();
            transaction.GasLimit = Hex.ToBytes(transactionJson.GasLimit).ToUnsignedBigInteger();
            transaction.GasPrice = Hex.ToBytes(transactionJson.GasPrice).ToUnsignedBigInteger();
            transaction.Nonce = Hex.ToBytes(transactionJson.Nonce).ToUnsignedBigInteger();
            transaction.To = string.IsNullOrWhiteSpace(transactionJson.To) ? null : new Address(new Hex(transactionJson.To));
            transaction.Data = transaction.To == null ? null : Hex.ToBytes(transactionJson.Data);
            transaction.Init = transaction.To == null ? Hex.ToBytes(transactionJson.Data) : null;
            Signature signature = new Signature(
                Hex.ToBytes(transactionJson.R).PadLeft(32),
                Hex.ToBytes(transactionJson.S).PadLeft(32),
                Hex.ToBytes(transactionJson.V)[0]);
            transaction.Signature = signature;
            
            return transaction;
        }

        private static BlockchainTest Convert(string name, BlockchainTestJson testJson)
        {
            BlockchainTest test = new BlockchainTest();
            test.Name = name;
            test.Network = testJson.EthereumNetwork;
            test.LastBlockHash = new Keccak(testJson.LastBlockHash);
            test.GenesisRlp = new Rlp(Hex.ToBytes(testJson.GenesisRlp));
            test.GenesisBlockHeader = testJson.GenesisBlockHeader;
            test.Blocks = testJson.Blocks;
            test.PostState = testJson.PostState.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            return test;
        }

        public class TestBlockHeaderJson
        {
            public string Bloom { get; set; }
            public string Coinbase { get; set; }
            public string Difficulty { get; set; }
            public string ExtraData { get; set; }
            public string GasLimit { get; set; }
            public string GasUsed { get; set; }
            public string Hash { get; set; }
            public string MixHash { get; set; }
            public string Nonce { get; set; }
            public string Number { get; set; }
            public string ParentHash { get; set; }
            public string ReceiptTrie { get; set; }
            public string StateRoot { get; set; }
            public string Timestamp { get; set; }
            public string TransactionsTrie { get; set; }
            public string UncleHash { get; set; }
        }

        public class TestBlockHeader
        {
            public Bloom Bloom { get; set; }
            public Address Coinbase { get; set; }
            public BigInteger Difficulty { get; set; }
            public byte[] ExtraData { get; set; }
            public BigInteger GasLimit { get; set; }
            public BigInteger GasUsed { get; set; }
            public Keccak Hash { get; set; }
            public Keccak MixHash { get; set; }
            public BigInteger Nonce { get; set; }
            public BigInteger Number { get; set; }
            public Keccak ParentHash { get; set; }
            public Keccak ReceiptTrie { get; set; }
            public Keccak StateRoot { get; set; }
            public BigInteger Timestamp { get; set; }
            public Keccak TransactionsTrie { get; set; }
            public Keccak UncleHash { get; set; }
        }

        public class TestBlockJson
        {
            public TestBlockHeaderJson BlockHeader { get; set; }
            public TestBlockHeaderJson[] UncleHeaders { get; set; }
            public string Rlp { get; set; }
            public TransactionJson[] Transactions { get; set; }
        }

        public class TestBlock
        {
            public TestBlockHeader BlockHeader { get; set; }
            public TestBlockHeader[] UncleHeaders { get; set; }
            public string Rlp { get; set; }
            public IncomingTransaction[] Transactions { get; set; }
        }

        public class AccountState
        {
            public BigInteger Balance { get; set; }
            public byte[] Code { get; set; }
            public BigInteger Nonce { get; set; }
            public Dictionary<BigInteger, byte[]> Storage { get; set; }
        }

        public class AccountStateJson
        {
            public string Balance { get; set; }
            public string Code { get; set; }
            public string Nonce { get; set; }
            public Dictionary<string, string> Storage { get; set; }
        }

        public class TransactionJson
        {
            public string Data { get; set; }
            public string GasLimit { get; set; }
            public string GasPrice { get; set; }
            public string Nonce { get; set; }
            public string To { get; set; }
            public string Value { get; set; }
            public string R { get; set; }
            public string S { get; set; }
            public string V { get; set; }
        }

        public class IncomingTransaction
        {
            public byte[] Data { get; set; }
            public BigInteger GasLimit { get; set; }
            public BigInteger GasPrice { get; set; }
            public BigInteger Nonce { get; set; }
            public Address To { get; set; }
            public BigInteger Value { get; set; }
            public byte[] R { get; set; }
            public byte[] S { get; set; }
            public byte V { get; set; }
        }

        public class BlockchainTestJson
        {
            public string Network { get; set; }
            public EthereumNetwork EthereumNetwork { get; set; }
            public string LastBlockHash { get; set; }
            public string GenesisRlp { get; set; }

            public TestBlockJson[] Blocks { get; set; }
            public TestBlockHeaderJson GenesisBlockHeader { get; set; }

            public Dictionary<string, AccountStateJson> Pre { get; set; }
            public Dictionary<string, AccountStateJson> PostState { get; set; }
        }

        public class BlockchainTest
        {
            public string Name { get; set; }
            public EthereumNetwork Network { get; set; }
            public Keccak LastBlockHash { get; set; }
            public Rlp GenesisRlp { get; set; }

            public TestBlockJson[] Blocks { get; set; }
            public TestBlockHeaderJson GenesisBlockHeader { get; set; }

            public Dictionary<Address, AccountState> Pre { get; set; }
            public Dictionary<Address, AccountState> PostState { get; set; }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}
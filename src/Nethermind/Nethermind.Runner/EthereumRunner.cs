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

using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Store;

namespace Nethermind.Runner
{
    public class EthereumRunner : IEthereumRunner
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IStateProvider _stateProvider;
        private readonly IMultiDb _multiDb;

        public EthereumRunner(IJsonSerializer jsonSerializer, IBlockchainProcessor blockchainProcessor, IStateProvider stateProvider, IMultiDb multiDb)
        {
            _jsonSerializer = jsonSerializer;
            _blockchainProcessor = blockchainProcessor;
            _stateProvider = stateProvider;
            _multiDb = multiDb;
        }

        public void Start(string bootNodeValue, int discoveryPort, string genesisFile)
        {
            var genesisBlockRaw = File.ReadAllText(genesisFile);
            var blockJson = _jsonSerializer.Deserialize<TestGenesisJson>(genesisBlockRaw);
            var block = Convert(blockJson);    
            _blockchainProcessor.Initialize(Rlp.Encode(block));
            InitializeAccounts(blockJson.Alloc);
        }

        public void Stop()
        {
            
        }

        private static Block Convert(TestGenesisJson headerJson)
        {
            if (headerJson == null)
            {
                return null;
            }

            var header = new BlockHeader(
                new Keccak(headerJson.ParentHash),
                Keccak.OfAnEmptySequenceRlp,
                new Address(headerJson.Coinbase),
                Hex.ToBytes(headerJson.Difficulty).ToUnsignedBigInteger(),
                0,
                (long) Hex.ToBytes(headerJson.GasLimit).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.Timestamp).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.ExtraData)
            )
            {
                Bloom = new Bloom(),
                MixHash = new Keccak(headerJson.MixHash),
                Nonce = (ulong) Hex.ToBytes(headerJson.Nonce).ToUnsignedBigInteger(),               
                ReceiptsRoot = Keccak.EmptyTreeHash,
                StateRoot = Keccak.EmptyTreeHash,
                TransactionsRoot = Keccak.EmptyTreeHash
            };

            header.RecomputeHash();

            var block = new Block(header);
            return block;
        }

        private void InitializeAccounts(IDictionary<string, TestAccount> alloc)
        {
            foreach (var account in alloc)
            {
                _stateProvider.CreateAccount(new Address(new Hex(account.Key)), BigInteger.Parse(account.Value.Balance));
            }
            _multiDb.Commit();
        }
    }
}
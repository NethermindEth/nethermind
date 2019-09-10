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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;

namespace Ethereum.Test.Base
{
    public static class JsonToBlockchainTest
    {
        public static IReleaseSpec ParseSpec(string network)
        {
            switch (network)
            {
                case "Frontier":
                    return Frontier.Instance;
                case "Homestead":
                    return Homestead.Instance;
                case "TangerineWhistle":
                    return TangerineWhistle.Instance;
                case "SpuriousDragon":
                    return SpuriousDragon.Instance;
                case "EIP150":
                    return TangerineWhistle.Instance;
                case "EIP158":
                    return SpuriousDragon.Instance;
                case "Dao":
                    return Dao.Instance;
                case "Constantinople":
                    return Constantinople.Instance;
                case "ConstantinopleFix":
                    return ConstantinopleFix.Instance;
                case "Byzantium":
                    return Byzantium.Instance;
                case "Istanbul":
                    return Istanbul.Instance;
                default:
                    throw new NotSupportedException();
            }
        }
        
        public static BlockHeader Convert(TestBlockHeaderJson headerJson)
        {
            if (headerJson == null)
            {
                return null;
            }

            BlockHeader header = new BlockHeader(
                new Keccak(headerJson.ParentHash),
                new Keccak(headerJson.UncleHash),
                new Address(headerJson.Coinbase),
                Bytes.FromHexString(headerJson.Difficulty).ToUInt256(),
                (long)Bytes.FromHexString(headerJson.Number).ToUInt256(),
                (long)Bytes.FromHexString(headerJson.GasLimit).ToUnsignedBigInteger(),
                Bytes.FromHexString(headerJson.Timestamp).ToUInt256(),
                Bytes.FromHexString(headerJson.ExtraData)
            );

            header.Bloom = new Bloom(Bytes.FromHexString(headerJson.Bloom).ToBigEndianBitArray2048());
            header.GasUsed = (long) Bytes.FromHexString(headerJson.GasUsed).ToUnsignedBigInteger();
            header.Hash = new Keccak(headerJson.Hash);
            header.MixHash = new Keccak(headerJson.MixHash);
            header.Nonce = (ulong) Bytes.FromHexString(headerJson.Nonce).ToUnsignedBigInteger();
            header.ReceiptsRoot = new Keccak(headerJson.ReceiptTrie);
            header.StateRoot = new Keccak(headerJson.StateRoot);
            header.TxRoot = new Keccak(headerJson.TransactionsTrie);
            return header;
        }

        public static Block Convert(TestBlockJson testBlockJson)
        {
            BlockHeader header = Convert(testBlockJson.BlockHeader);
            BlockHeader[] ommers = testBlockJson.UncleHeaders?.Select(Convert).ToArray() ?? new BlockHeader[0];
            Block block = new Block(header, ommers);
            block.Body.Transactions = testBlockJson.Transactions?.Select(Convert).ToArray();
            return block;
        }

        public static Transaction Convert(TransactionJson transactionJson)
        {
            Transaction transaction = new Transaction();
            transaction.Value = Bytes.FromHexString(transactionJson.Value).ToUInt256();
            transaction.GasLimit = Bytes.FromHexString(transactionJson.GasLimit).ToInt64();
            transaction.GasPrice = Bytes.FromHexString(transactionJson.GasPrice).ToUInt256();
            transaction.Nonce = Bytes.FromHexString(transactionJson.Nonce).ToUInt256();
            transaction.To = string.IsNullOrWhiteSpace(transactionJson.To) ? null : new Address(transactionJson.To);
            transaction.Data = transaction.To == null ? null : Bytes.FromHexString(transactionJson.Data);
            transaction.Init = transaction.To == null ? Bytes.FromHexString(transactionJson.Data) : null;
            Signature signature = new Signature(
                Bytes.FromHexString(transactionJson.R).PadLeft(32),
                Bytes.FromHexString(transactionJson.S).PadLeft(32),
                Bytes.FromHexString(transactionJson.V)[0]);
            transaction.Signature = signature;

            return transaction;
        }

        private static AccountState Convert(AccountStateJson accountStateJson)
        {
            AccountState state = new AccountState();
            state.Balance = Bytes.FromHexString(accountStateJson.Balance).ToUInt256();
            state.Code = Bytes.FromHexString(accountStateJson.Code);
            state.Nonce = Bytes.FromHexString(accountStateJson.Nonce).ToUInt256();
            state.Storage = accountStateJson.Storage.ToDictionary(
                p => Bytes.FromHexString(p.Key).ToUInt256(),
                p => Bytes.FromHexString(p.Value));
            return state;
        }
        
        public static BlockchainTest Convert(string name, BlockchainTestJson testJson)
        {
            if (testJson.LoadFailure != null)
            {
                return new BlockchainTest
                {
                    Name = name,
                    LoadFailure = testJson.LoadFailure
                };
            }

            BlockchainTest test = new BlockchainTest();
            test.Name = name;
            test.Network = testJson.EthereumNetwork;
            test.NetworkAfterTransition = testJson.EthereumNetworkAfterTransition;
            test.TransitionBlockNumber = testJson.TransitionBlockNumber;
            test.LastBlockHash = new Keccak(testJson.LastBlockHash);
            test.GenesisRlp = testJson.GenesisRlp == null ? null : new Rlp(Bytes.FromHexString(testJson.GenesisRlp));
            test.GenesisBlockHeader = testJson.GenesisBlockHeader;
            test.Blocks = testJson.Blocks;
            test.PostState = testJson.PostState.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            test.Pre = testJson.Pre.ToDictionary(p => new Address(p.Key), p => Convert(p.Value));
            return test;
        }
    }
}
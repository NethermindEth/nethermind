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
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Specs.ChainSpec
{
    public class ChainSpecLoader : IChainSpecLoader
    {
        private readonly IJsonSerializer _serializer;

        public ChainSpecLoader(IJsonSerializer serializer)
        {
            _serializer = serializer;
        }

        public ChainSpec Load(byte[] data)
        {
            string jsonData = System.Text.Encoding.UTF8.GetString(data);
            ChainSpecJson chainSpecJson = _serializer.Deserialize<ChainSpecJson>(jsonData);
            ChainSpec chainSpec = new ChainSpec();
            chainSpec.ChainId = ToInt(chainSpecJson.Params.NetworkId);
            chainSpec.Name = chainSpecJson.Name;

            ulong nonce = ToULong(chainSpecJson.Genesis.Seal.Ethereum.Nonce);
            Keccak mixHash = HexToKeccak(chainSpecJson.Genesis.Seal.Ethereum.MixHash);
            Keccak parentHash = HexToKeccak(chainSpecJson.Genesis.ParentHash);
            BigInteger timestamp = HexToBigInteger(chainSpecJson.Genesis.Timestamp);
            BigInteger difficulty = HexToBigInteger(chainSpecJson.Genesis.Difficulty);
            byte[] extraData = new Hex(chainSpecJson.Genesis.ExtraData);
            long gasLimit = HexToLong(chainSpecJson.Genesis.GasLimit);
            Address beneficiary = HexToAddress(chainSpecJson.Genesis.Author);

            BlockHeader genesisHeader = new BlockHeader(
                parentHash,
                Keccak.OfAnEmptySequenceRlp,
                beneficiary,
                difficulty,
                0,
                gasLimit,
                timestamp,
                extraData);
            
            genesisHeader.Hash = Keccak.Zero; // need to run the block to know the actual hash
            
            genesisHeader.Bloom = new Bloom();
            genesisHeader.GasUsed = 0;
            genesisHeader.MixHash = mixHash;
            genesisHeader.Nonce = nonce;
            genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            genesisHeader.StateRoot = Keccak.EmptyTreeHash;
            genesisHeader.TransactionsRoot = Keccak.EmptyTreeHash;

            chainSpec.Genesis = new Block(genesisHeader);
            
            chainSpec.Allocations = new Dictionary<Address, BigInteger>();
            foreach (KeyValuePair<string, ChainSpecAccountJson> account in chainSpecJson.Accounts)
            {
                chainSpec.Allocations[new Address(account.Key)] = BigInteger.Parse(account.Value?.Balance ?? "0");
            }
            
            chainSpec.NetworkNodes = new NetworkNode[chainSpecJson.Nodes.Length];
            for (int i = 0; i < chainSpecJson.Nodes.Length; i++)
            {
                chainSpec.NetworkNodes[i] = new NetworkNode(chainSpecJson.Nodes[i], $"bootnode{i}");
            }

            return chainSpec;
        }

        private static Address HexToAddress(string hexNumber)
        {
            return new Address(new Hex(hexNumber));
        }

        private static Keccak HexToKeccak(string hexNumber)
        {
            return new Keccak(new Hex(hexNumber));
        }

        private static long HexToLong(string hexNumber)
        {
            return (long)HexToBigInteger(hexNumber);
        }

        private static ulong ToULong(string hexNumber)
        {
            return (ulong)HexToBigInteger(hexNumber);
        }

        private static int ToInt(string hexNumber)
        {
            return (int)HexToBigInteger(hexNumber);
        }

        private static BigInteger HexToBigInteger(string hexNumber)
        {
            return new Hex(hexNumber).ToUnsignedBigInteger();
        }
    }
}
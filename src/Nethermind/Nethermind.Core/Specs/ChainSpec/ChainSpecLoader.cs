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
using System.Globalization;
using System.IO;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

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
            try
            {
                string jsonData = System.Text.Encoding.UTF8.GetString(data);
                var chainSpecJson = _serializer.Deserialize<ChainSpecJson>(jsonData);
                var chainSpec = new ChainSpec();

                chainSpec.ChainId = ToInt(chainSpecJson.Params.NetworkId);
                chainSpec.Name = chainSpecJson.Name;
                LoadGenesis(chainSpecJson, chainSpec);
                LoadAllocations(chainSpec, chainSpecJson);
                LoadBootnodes(chainSpecJson, chainSpec);

                return chainSpec;
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Error when loading chainspec", e);
            }
        }

        private static void LoadGenesis(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            var nonce = ToULong(chainSpecJson.Genesis.Seal.Ethereum.Nonce);
            var mixHash = HexToKeccak(chainSpecJson.Genesis.Seal.Ethereum.MixHash);
            var parentHash = HexToKeccak(chainSpecJson.Genesis.ParentHash);
            var timestamp = HexToUInt256(chainSpecJson.Genesis.Timestamp);
            var difficulty = HexToUInt256(chainSpecJson.Genesis.Difficulty);
            var extraData = Bytes.FromHexString(chainSpecJson.Genesis.ExtraData);
            var gasLimit = HexToLong(chainSpecJson.Genesis.GasLimit);
            var beneficiary = new Address(chainSpecJson.Genesis.Author);

            BlockHeader genesisHeader = new BlockHeader(
                parentHash,
                Keccak.OfAnEmptySequenceRlp,
                beneficiary,
                difficulty,
                0,
                gasLimit,
                timestamp,
                extraData);

            genesisHeader.Author = beneficiary;
            genesisHeader.Hash = Keccak.Zero; // need to run the block to know the actual hash
            genesisHeader.Bloom = new Bloom();
            genesisHeader.GasUsed = 0;
            genesisHeader.MixHash = mixHash;
            genesisHeader.Nonce = nonce;
            genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            genesisHeader.StateRoot = Keccak.EmptyTreeHash;
            genesisHeader.TransactionsRoot = Keccak.EmptyTreeHash;

            chainSpec.Genesis = new Block(genesisHeader);
        }

        private static void LoadAllocations(ChainSpec chainSpec, ChainSpecJson chainSpecJson)
        {
            if (chainSpecJson.Accounts == null)
            {
                return;
            }

            chainSpec.Allocations = new Dictionary<Address, UInt256>();
            foreach (KeyValuePair<string, ChainSpecAccountJson> account in chainSpecJson.Accounts)
            {
                if (account.Value.Balance != null)
                {
                    bool result = UInt256.TryParse(account.Value.Balance, out UInt256 allocationValue);
                    if (!result)
                    {
                        result = UInt256.TryParse(account.Value.Balance.Replace("0x", string.Empty), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out allocationValue);
                    }

                    if (!result)
                    {
                        throw new InvalidDataException($"Cannot recognize allocation value format in {account.Value.Balance}");
                    }
                    
                    chainSpec.Allocations[new Address(account.Key)] = allocationValue;
                }
            }
        }

        private static void LoadBootnodes(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Nodes == null)
            {
                return;
            }

            chainSpec.NetworkNodes = new NetworkNode[chainSpecJson.Nodes.Length];
            for (int i = 0; i < chainSpecJson.Nodes.Length; i++)
            {
                chainSpec.NetworkNodes[i] = new NetworkNode(chainSpecJson.Nodes[i], $"bootnode{i}");
            }
        }

        private static Keccak HexToKeccak(string hexNumber)
        {
            return new Keccak(Bytes.FromHexString(hexNumber));
        }

        private static long HexToLong(string hexNumber)
        {
            return (long) HexToBigInteger(hexNumber);
        }

        private static ulong ToULong(string hexNumber)
        {
            return (ulong) HexToBigInteger(hexNumber);
        }

        private static int ToInt(string hexNumber)
        {
            return (int) HexToBigInteger(hexNumber);
        }

        private static BigInteger HexToBigInteger(string hexNumber)
        {
            return Bytes.FromHexString(hexNumber).ToUnsignedBigInteger();
        }

        private static UInt256 HexToUInt256(string hexNumber)
        {
            UInt256.CreateFromBigEndian(out UInt256 result, Bytes.FromHexString(hexNumber));
            return result;
        }
    }
}
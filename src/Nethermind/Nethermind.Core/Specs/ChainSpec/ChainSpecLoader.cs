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
                chainSpec.DataDir = chainSpecJson.DataDir;
                LoadGenesis(chainSpecJson, chainSpec);
                LoadEngine(chainSpecJson, chainSpec);
                LoadAllocations(chainSpec, chainSpecJson);
                LoadBootnodes(chainSpecJson, chainSpec);

                return chainSpec;
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Error when loading chainspec ({e.Message})", e);
            }
        }

        private void LoadEngine(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Engine?.AuthorityRound != null)
            {
                chainSpec.SealEngineType = SealEngineType.AuRa;
            }
            else if (chainSpecJson.Engine?.Clique != null)
            {
                chainSpec.SealEngineType = SealEngineType.Clique;
                chainSpec.SealEngineParams = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
                foreach (var param in chainSpecJson.Engine.Clique.Params)
                {
                    chainSpec.SealEngineParams.Add(param.Key, param.Value);
                }
            }
            else if (chainSpecJson.Engine?.Ethash != null)
            {
                chainSpec.SealEngineType = SealEngineType.Ethash;
            }
            else if (chainSpecJson.Engine?.NethDev != null)
            {
                chainSpec.SealEngineType = SealEngineType.NethDev;
            }
            else
            {
                throw new NotSupportedException("unknown seal engine in chainspec");
            }
        }

        private static void LoadGenesis(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Genesis == null)
            {
                return;
            }
            
            var nonce = ToULong(chainSpecJson.Genesis.Seal.Ethereum?.Nonce ?? "0x0");
            var mixHash = HexToKeccak(chainSpecJson.Genesis.Seal.Ethereum?.MixHash ?? Keccak.Zero.ToString(true));
            var parentHash = HexToKeccak(chainSpecJson.Genesis.ParentHash ?? Keccak.Zero.ToString(true));
            var timestamp = HexToUInt256(chainSpecJson.Genesis.Timestamp ?? "0x0");
            var difficulty = HexToUInt256(chainSpecJson.Genesis.Difficulty ?? "0x0");
            var extraData = Bytes.FromHexString(chainSpecJson.Genesis.ExtraData ?? "0x");
            var gasLimit = HexToLong(chainSpecJson.Genesis.GasLimit ?? "0x0");
            var beneficiary = new Address(chainSpecJson.Genesis.Author ?? Address.Zero.ToString());

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
                chainSpec.Bootnodes = new NetworkNode[0];
                return;
            }

            chainSpec.Bootnodes = new NetworkNode[chainSpecJson.Nodes.Length];
            for (int i = 0; i < chainSpecJson.Nodes.Length; i++)
            {
                chainSpec.Bootnodes[i] = new NetworkNode(chainSpecJson.Nodes[i], $"bootnode{i}");
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
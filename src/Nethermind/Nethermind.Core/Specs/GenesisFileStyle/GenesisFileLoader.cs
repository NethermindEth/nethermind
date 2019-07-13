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
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.GenesisFileStyle.Json;

namespace Nethermind.Core.Specs.GenesisFileStyle
{
    /// <summary>
    /// This class can load a Geth-style genesis file and build a <see cref="ChainSpec"/> out of it. 
    /// </summary>
    public class GenesisFileLoader : IChainSpecLoader
    {
        private readonly IJsonSerializer _serializer;

        public GenesisFileLoader(IJsonSerializer serializer)
        {
            _serializer = serializer;
        }

        public ChainSpec Load(byte[] data)
        {
            try
            {
                string jsonData = System.Text.Encoding.UTF8.GetString(data);
                var genesisJson = _serializer.Deserialize<GenesisFileJson>(jsonData);
                var chainSpec = new ChainSpec();

                chainSpec.ChainId = (int) genesisJson.Config.ChainId;
                LoadGenesis(genesisJson, chainSpec);
                LoadEngine(genesisJson, chainSpec);
                LoadAllocations(genesisJson, chainSpec);

                chainSpec.HomesteadBlockNumber = genesisJson.Config.HomesteadBlock;
                chainSpec.TangerineWhistleBlockNumber = genesisJson.Config.Eip150Block;
                chainSpec.SpuriousDragonBlockNumber = genesisJson.Config.Eip158Block;
                chainSpec.ByzantiumBlockNumber = genesisJson.Config.ByzantiumBlock;
                chainSpec.ConstantinopleBlockNumber = genesisJson.Config.ConstantinopleBlock;

                return chainSpec;
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Error when loading chainspec ({e.Message})", e);
            }
        }

        private void LoadEngine(GenesisFileJson genesisJson, ChainSpec chainSpec)
        {
            if (genesisJson.Config.Clique != null)
            {
                chainSpec.SealEngineType = SealEngineType.Clique;
                chainSpec.Clique = new CliqueParameters();
                chainSpec.Clique.Period = genesisJson.Config.Clique.Period;
                chainSpec.Clique.Epoch = genesisJson.Config.Clique.Epoch;
                chainSpec.Clique.Reward = 0;
            }
            else
            {
                chainSpec.SealEngineType = SealEngineType.Ethash;
            }
        }

        private static void LoadGenesis(GenesisFileJson genesisJson, ChainSpec chainSpec)
        {
            var nonce = genesisJson.Nonce;
            var mixHash = genesisJson.MixHash;
            var parentHash = genesisJson.ParentHash ?? Keccak.Zero;
            var timestamp = genesisJson.Timestamp;
            var difficulty = genesisJson.Difficulty;
            var extraData = genesisJson.ExtraData;
            var gasLimit = genesisJson.GasLimit;
            var beneficiary = genesisJson.Author ?? Address.Zero;

            BlockHeader genesisHeader = new BlockHeader(
                parentHash,
                Keccak.OfAnEmptySequenceRlp,
                beneficiary,
                difficulty,
                0,
                (long) gasLimit,
                timestamp,
                extraData);

            genesisHeader.Author = beneficiary;
            genesisHeader.Hash = Keccak.Zero; // need to run the block to know the actual hash
            genesisHeader.Bloom = new Bloom();
            genesisHeader.GasUsed = 0;
            genesisHeader.MixHash = mixHash;
            genesisHeader.Nonce = (ulong) nonce;
            genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            genesisHeader.StateRoot = Keccak.EmptyTreeHash;
            genesisHeader.TxRoot = Keccak.EmptyTreeHash;

            chainSpec.Genesis = new Block(genesisHeader);
        }

        private static void LoadAllocations(GenesisFileJson genesisJson, ChainSpec chainSpec)
        {
            if (genesisJson.Alloc == null)
            {
                return;
            }

            chainSpec.Allocations = new Dictionary<Address, ChainSpecAllocation>();
            foreach ((string addressString, AllocationJson allocation) in genesisJson.Alloc)
            {
                if (allocation.BuiltIn != null)
                {
                    continue;
                }

                chainSpec.Allocations[new Address(addressString)] = new ChainSpecAllocation(allocation.Balance);
            }
        }
    }
}
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
    /// <summary>
    /// This class can load a Parity-style chain spec file and build a <see cref="ChainSpec"/> out of it. 
    /// </summary>
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

                chainSpec.ChainId = (int)chainSpecJson.Params.NetworkId;
                chainSpec.Name = chainSpecJson.Name;
                chainSpec.DataDir = chainSpecJson.DataDir;
                LoadGenesis(chainSpecJson, chainSpec);
                LoadEngine(chainSpecJson, chainSpec);
                LoadAllocations(chainSpecJson, chainSpec);
                LoadBootnodes(chainSpecJson, chainSpec);
                LoadTransitions(chainSpecJson, chainSpec);

                return chainSpec;
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Error when loading chainspec ({e.Message})", e);
            }
        }

        private void LoadTransitions(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Engine?.Ethash != null)
            {
                chainSpec.HomesteadBlockNumber = (UInt256?) chainSpecJson.Engine.Ethash.HomesteadTransition;
                chainSpec.DaoForkBlockNumber = (UInt256?) chainSpecJson.Engine.Ethash.DaoHardForkTransition;
            }
            
            chainSpec.TangerineWhistleBlockNumber = (UInt256?) chainSpecJson.Params.Eip150Transition;
            chainSpec.SpuriousDragonBlockNumber = (UInt256?) chainSpecJson.Params.Eip160Transition;
            chainSpec.ByzantiumBlockNumber = (UInt256?) chainSpecJson.Params.Eip140Transition;
            chainSpec.ConstantinopleBlockNumber = (UInt256?) chainSpecJson.Params.Eip145Transition;
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
                chainSpec.CliqueEpoch = chainSpecJson.Engine.Clique.Epoch;
                chainSpec.CliquePeriod = chainSpecJson.Engine.Clique.Period;
                chainSpec.CliqueReward = chainSpecJson.Engine.Clique.BlockReward ?? UInt256.Zero;
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

            var nonce = chainSpecJson.Genesis.Seal.Ethereum?.Nonce ?? 0;
            var mixHash = chainSpecJson.Genesis.Seal.Ethereum?.MixHash ?? Keccak.Zero;
            var parentHash = chainSpecJson.Genesis.ParentHash;
            var timestamp = chainSpecJson.Genesis.Timestamp;
            var difficulty = chainSpecJson.Genesis.Difficulty;
            var extraData = chainSpecJson.Genesis.ExtraData;
            var gasLimit = chainSpecJson.Genesis.GasLimit;
            var beneficiary = chainSpecJson.Genesis.Author ?? Address.Zero;

            BlockHeader genesisHeader = new BlockHeader(
                parentHash,
                Keccak.OfAnEmptySequenceRlp,
                beneficiary,
                difficulty,
                0,
                (long)gasLimit,
                timestamp,
                extraData);

            genesisHeader.Author = beneficiary;
            genesisHeader.Hash = Keccak.Zero; // need to run the block to know the actual hash
            genesisHeader.Bloom = new Bloom();
            genesisHeader.GasUsed = 0;
            genesisHeader.MixHash = mixHash;
            genesisHeader.Nonce = (ulong)nonce;
            genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            genesisHeader.StateRoot = Keccak.EmptyTreeHash;
            genesisHeader.TransactionsRoot = Keccak.EmptyTreeHash;

            chainSpec.Genesis = new Block(genesisHeader);
        }

        private static void LoadAllocations(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Accounts == null)
            {
                return;
            }

            chainSpec.Allocations = new Dictionary<Address, UInt256>();
            foreach (KeyValuePair<string, AllocationJson> account in chainSpecJson.Accounts)
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
                chainSpec.Bootnodes[i] = new NetworkNode(chainSpecJson.Nodes[i]);
            }
        }
    }
}
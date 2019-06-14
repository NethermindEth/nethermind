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
using Nethermind.Core.Json;
using Nethermind.Core.Specs.ChainSpecStyle.Json;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.ChainSpecStyle
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

                chainSpec.ChainId = (int) chainSpecJson.Params.NetworkId;
                chainSpec.Name = chainSpecJson.Name;
                chainSpec.DataDir = chainSpecJson.DataDir;
                LoadGenesis(chainSpecJson, chainSpec);
                LoadEngine(chainSpecJson, chainSpec);
                LoadAllocations(chainSpecJson, chainSpec);
                LoadBootnodes(chainSpecJson, chainSpec);
                LoadTransitions(chainSpecJson, chainSpec);
                LoadParameters(chainSpecJson, chainSpec);

                return chainSpec;
            }
            catch (Exception e)
            {
                throw new InvalidDataException($"Error when loading chainspec ({e.Message})", e);
            }
        }

        private void LoadParameters(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            chainSpec.Parameters = new ChainParameters();
            chainSpec.Parameters.AccountStartNonce = chainSpecJson.Params.AccountStartNonce ?? UInt256.Zero;
            chainSpec.Parameters.GasLimitBoundDivisor= chainSpecJson.Params.GasLimitBoundDivisor ?? 0x0400;
            chainSpec.Parameters.MaximumExtraDataSize = chainSpecJson.Params.MaximumExtraDataSize ?? 32;
            chainSpec.Parameters.MinGasLimit = chainSpecJson.Params.MinGasLimit ?? 5000;
            chainSpec.Parameters.MaxCodeSize = chainSpecJson.Params.MaxCodeSize ?? 24576;
            chainSpec.Parameters.MaxCodeSizeTransition = chainSpecJson.Params.MaxCodeSizeTransition ?? 0;
            chainSpec.Parameters.Registrar = chainSpecJson.Params.EnsRegistrar;
            chainSpec.Parameters.ForkBlock = chainSpecJson.Params.ForkBlock;
            chainSpec.Parameters.ForkCanonHash = chainSpecJson.Params.ForkCanonHash;
            chainSpec.Parameters.Eip150Transition = chainSpecJson.Params.Eip150Transition;
            chainSpec.Parameters.Eip160Transition = chainSpecJson.Params.Eip160Transition;
            chainSpec.Parameters.Eip161abcTransition = chainSpecJson.Params.Eip161abcTransition;
            chainSpec.Parameters.Eip161dTransition = chainSpecJson.Params.Eip161dTransition;
            chainSpec.Parameters.Eip155Transition = chainSpecJson.Params.Eip155Transition;
            chainSpec.Parameters.Eip140Transition = chainSpecJson.Params.Eip140Transition;
            chainSpec.Parameters.Eip211Transition = chainSpecJson.Params.Eip211Transition;
            chainSpec.Parameters.Eip214Transition = chainSpecJson.Params.Eip214Transition;
            chainSpec.Parameters.Eip658Transition = chainSpecJson.Params.Eip658Transition;
            chainSpec.Parameters.Eip145Transition = chainSpecJson.Params.Eip145Transition;
            chainSpec.Parameters.Eip1014Transition = chainSpecJson.Params.Eip1014Transition;
            chainSpec.Parameters.Eip1052Transition = chainSpecJson.Params.Eip1052Transition;
            chainSpec.Parameters.Eip1283Transition = chainSpecJson.Params.Eip1283Transition;
            chainSpec.Parameters.Eip1283DisableTransition = chainSpecJson.Params.Eip1283DisableTransition;
        }

        private void LoadTransitions(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Engine?.Ethash != null)
            {
                chainSpec.HomesteadBlockNumber = chainSpecJson.Engine.Ethash.HomesteadTransition;
                chainSpec.DaoForkBlockNumber = chainSpecJson.Engine.Ethash.DaoHardForkTransition;
            }

            chainSpec.TangerineWhistleBlockNumber = chainSpecJson.Params.Eip150Transition;
            chainSpec.SpuriousDragonBlockNumber = chainSpecJson.Params.Eip160Transition;
            chainSpec.ByzantiumBlockNumber = chainSpecJson.Params.Eip140Transition;
            chainSpec.ConstantinopleBlockNumber = chainSpecJson.Params.Eip145Transition;
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
                chainSpec.Clique = new CliqueParameters();
                chainSpec.Clique.Epoch = chainSpecJson.Engine.Clique.Epoch;
                chainSpec.Clique.Period = chainSpecJson.Engine.Clique.Period;
                chainSpec.Clique.Reward = chainSpecJson.Engine.Clique.BlockReward ?? UInt256.Zero;
            }
            else if (chainSpecJson.Engine?.Ethash != null)
            {
                chainSpec.SealEngineType = SealEngineType.Ethash;
                chainSpec.Ethash = new EthashParameters();
                chainSpec.Ethash.MinimumDifficulty = chainSpecJson.Engine.Ethash.MinimumDifficulty ?? 0L;
                chainSpec.Ethash.DifficultyBoundDivisor = chainSpecJson.Engine.Ethash.DifficultyBoundDivisor ?? 0x0800L;
                chainSpec.Ethash.DurationLimit = chainSpecJson.Engine.Ethash.DurationLimit ?? 13L;
                chainSpec.Ethash.HomesteadTransition = chainSpecJson.Engine.Ethash.HomesteadTransition ?? 0;
                chainSpec.Ethash.DaoHardforkTransition = chainSpecJson.Engine.Ethash.DaoHardforkTransition;
                chainSpec.Ethash.DaoHardforkBeneficiary = chainSpecJson.Engine.Ethash.DaoHardforkBeneficiary;
                chainSpec.Ethash.DaoHardforkAccounts = chainSpecJson.Engine.Ethash.DaoHardforkAccounts ?? new Address[0];
                chainSpec.Ethash.Eip100bTransition = chainSpecJson.Engine.Ethash.Eip100bTransition ?? 0L;

                chainSpec.Ethash.BlockRewards = new Dictionary<long, UInt256>();
                foreach (KeyValuePair<string, UInt256> reward in chainSpecJson.Engine.Ethash.BlockReward)
                {
                    chainSpec.Ethash.BlockRewards.Add(LongConverter.FromString(reward.Key), reward.Value);
                }

                chainSpec.Ethash.DifficultyBombDelays = new Dictionary<long, long>();
                foreach (KeyValuePair<string, long> reward in chainSpecJson.Engine.Ethash.DifficultyBombDelays)
                {
                    chainSpec.Ethash.DifficultyBombDelays.Add(LongConverter.FromString(reward.Key), reward.Value);
                }
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

        private static void LoadAllocations(ChainSpecJson chainSpecJson, ChainSpec chainSpec)
        {
            if (chainSpecJson.Accounts == null)
            {
                return;
            }

            chainSpec.Allocations = new Dictionary<Address, (UInt256 Balance, byte[] Code)>();
            foreach (KeyValuePair<string, AllocationJson> account in chainSpecJson.Accounts)
            {
                byte[] codeValue = null;
                UInt256 allocationValue = UInt256.Zero;
                if (account.Value.Balance != null)
                {
                    bool balanceParsingResult = UInt256.TryParse(account.Value.Balance, out allocationValue);
                    if (!balanceParsingResult)
                    {
                        balanceParsingResult = UInt256.TryParse(account.Value.Balance.Replace("0x", string.Empty), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out allocationValue);
                    }

                    if (!balanceParsingResult)
                    {
                        throw new InvalidDataException($"Cannot recognize allocation value format in {account.Value.Balance}");
                    }
                }

                if (account.Value.Code != null)
                {
                    codeValue = Bytes.FromHexString(account.Value.Code);
                }

                if (account.Value.Balance != null || account.Value.Code != null)
                {
                    chainSpec.Allocations[new Address(account.Key)] = (allocationValue, codeValue);
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
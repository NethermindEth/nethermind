// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Xdc;

/// <summary>
/// XDPoS checkpoint reward calculator
/// 
/// Algorithm from geth-xdc and erigon-xdc:
/// 1. At block N where N % 900 == 0 and N > 2*900 (first real reward at 1800)
/// 2. Calculate chain reward: 250 XDC with halving at 2 and 5 years
/// 3. Get masternodes from previous checkpoint header
/// 4. Distribute equally among masternodes (simplified erigon approach)
/// 5. For each masternode: 90% to owner, 10% to foundation
/// 
/// Foundation wallet: 0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65
/// </summary>
public class XdcRewardCalculator : IRewardCalculator, IRewardCalculatorSource
{
    // Contract addresses
    private static readonly Address MasternodeVotingAddress = 
        new("0x0000000000000000000000000000000000000088");
    
    private static readonly Address BlockSignersAddress = 
        new("0x0000000000000000000000000000000000000089");
    
    private static readonly Address MainnetFoundationWallet = 
        new("0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65");
    
    // Constants
    private const long RewardCheckpoint = 900;
    private const long BaseRewardXdc = 250;  // 250 XDC per checkpoint
    private const long BlocksPerYear = 15768000;
    private const long TIPNoHalvingMNReward = 38383838;
    
    // Reward distribution percentages
    private const int RewardMasterPercent = 90;
    private const int RewardFoundationPercent = 10;
    
    // Storage slot for validatorsState mapping in 0x88 contract
    private const ulong ValidatorsStateSlot = 1;

    private readonly IBlockTree _blockTree;
    private readonly IWorldState _worldState;
    private readonly ILogger _logger;

    public XdcRewardCalculator(
        ILogManager logManager,
        IBlockTree blockTree,
        IWorldState worldState)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _logger = logManager?.GetClassLogger() ?? NullLogger.Instance;
    }

    public BlockReward[] CalculateRewards(Block block)
    {
        long number = block.Number;
        
        // Only apply rewards at checkpoint blocks
        // Condition from geth-xdc: number > 0 && number % rCheckpoint == 0 && number - rCheckpoint > 0
        // This means block 900 gets NO rewards (900-900=0, not > 0)
        // First actual reward is at block 1800
        if (block.IsGenesis || number % RewardCheckpoint != 0 || number <= RewardCheckpoint)
        {
            return Array.Empty<BlockReward>();
        }

        try
        {
            Console.WriteLine($"[XDC-REWARD] ====== Block {number} Checkpoint Reward Calculation ======");
            
            // Calculate chain reward with inflation halving
            UInt256 chainReward = CalculateChainReward((ulong)number);
            Console.WriteLine($"[XDC-REWARD] Chain reward: {chainReward} wei ({chainReward / Unit.Ether} XDC)");

            // Get masternodes from the PREVIOUS checkpoint
            // For block 1800: prevCheckpoint = 900
            long prevCheckpoint = number - RewardCheckpoint;
            Address[] masternodes = GetMasternodesFromCheckpoint(prevCheckpoint);
            
            if (masternodes.Length == 0)
            {
                Console.WriteLine($"[XDC-REWARD] No masternodes found at checkpoint {prevCheckpoint}");
                _logger.Warn($"No masternodes found at checkpoint {prevCheckpoint}");
                return Array.Empty<BlockReward>();
            }
            
            Console.WriteLine($"[XDC-REWARD] Found {masternodes.Length} masternodes at checkpoint {prevCheckpoint}");
            foreach (var mn in masternodes)
            {
                Console.WriteLine($"[XDC-REWARD]   Masternode: {mn}");
            }

            // Calculate reward per masternode (equal distribution like erigon)
            UInt256 rewardPerMasternode = chainReward / (ulong)masternodes.Length;
            Console.WriteLine($"[XDC-REWARD] Reward per masternode: {rewardPerMasternode / Unit.Ether} XDC");

            // Distribute rewards
            var rewards = new List<BlockReward>();
            var foundationTotal = UInt256.Zero;

            foreach (var masternode in masternodes)
            {
                // Get owner from validator contract
                Address owner = GetCandidateOwner(masternode);
                if (owner == Address.Zero)
                {
                    owner = masternode;  // Fallback to masternode if no owner found
                }

                // 90% to owner
                UInt256 ownerReward = rewardPerMasternode * RewardMasterPercent / 100;
                
                // 10% to foundation
                UInt256 foundationReward = rewardPerMasternode * RewardFoundationPercent / 100;
                foundationTotal += foundationReward;

                rewards.Add(new BlockReward(owner, ownerReward, BlockRewardType.Block));
                Console.WriteLine($"[XDC-REWARD]   {masternode} -> owner {owner}: {ownerReward / Unit.Ether} XDC");
            }

            // Add foundation reward (aggregated)
            rewards.Add(new BlockReward(MainnetFoundationWallet, foundationTotal, BlockRewardType.External));
            Console.WriteLine($"[XDC-REWARD] Foundation total: {foundationTotal / Unit.Ether} XDC");

            // Aggregate rewards by address (owner might be same for multiple masternodes)
            var aggregated = AggregateRewards(rewards);
            
            Console.WriteLine($"[XDC-REWARD] Total rewards: {aggregated.Length} recipients");
            UInt256 totalDistributed = UInt256.Zero;
            foreach (var r in aggregated)
            {
                Console.WriteLine($"[XDC-REWARD]   {r.Address}: {r.Value / Unit.Ether} XDC");
                totalDistributed += r.Value;
            }
            Console.WriteLine($"[XDC-REWARD] Total distributed: {totalDistributed / Unit.Ether} XDC");
            Console.WriteLine($"[XDC-REWARD] ====== End Block {number} ======");
            
            return aggregated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XDC-REWARD] ERROR: {ex.Message}");
            Console.WriteLine($"[XDC-REWARD] Stack: {ex.StackTrace}");
            _logger.Error($"Error calculating rewards for block {number}: {ex}");
            return Array.Empty<BlockReward>();
        }
    }

    /// <summary>
    /// Calculate chain reward with inflation halving.
    /// </summary>
    private UInt256 CalculateChainReward(ulong blockNumber)
    {
        UInt256 chainReward = (UInt256)BaseRewardXdc * Unit.Ether;
        
        // Apply halving if before TIPNoHalvingMNReward
        if (blockNumber < (ulong)TIPNoHalvingMNReward)
        {
            ulong twoYears = (ulong)BlocksPerYear * 2;
            ulong fiveYears = (ulong)BlocksPerYear * 5;
            
            if (blockNumber >= twoYears && blockNumber < fiveYears)
            {
                chainReward /= 2;  // Half reward
            }
            else if (blockNumber >= fiveYears)
            {
                chainReward /= 4;  // Quarter reward
            }
        }
        
        return chainReward;
    }

    /// <summary>
    /// Get masternodes from a checkpoint block header's extra data.
    /// V1 format: [32 bytes vanity][N*20 bytes signers][65 bytes seal]
    /// </summary>
    private Address[] GetMasternodesFromCheckpoint(long checkpointNumber)
    {
        BlockHeader? header = _blockTree.FindHeader(checkpointNumber);
        if (header is null)
        {
            Console.WriteLine($"[XDC-REWARD] Could not find header for checkpoint {checkpointNumber}");
            return Array.Empty<Address>();
        }

        byte[]? extraData = header.ExtraData;
        if (extraData is null || extraData.Length < 32 + 65)
        {
            Console.WriteLine($"[XDC-REWARD] ExtraData too short at checkpoint {checkpointNumber}: {extraData?.Length ?? 0} bytes");
            return Array.Empty<Address>();
        }

        // Calculate signers section length
        int signersDataLength = extraData.Length - 32 - 65;
        if (signersDataLength <= 0 || signersDataLength % 20 != 0)
        {
            Console.WriteLine($"[XDC-REWARD] Invalid signers data length at checkpoint {checkpointNumber}: {signersDataLength}");
            return Array.Empty<Address>();
        }

        int signerCount = signersDataLength / 20;
        var signers = new Address[signerCount];

        for (int i = 0; i < signerCount; i++)
        {
            byte[] addressBytes = new byte[20];
            Buffer.BlockCopy(extraData, 32 + (i * 20), addressBytes, 0, 20);
            signers[i] = new Address(addressBytes);
        }

        Console.WriteLine($"[XDC-REWARD] Extracted {signerCount} masternodes from checkpoint {checkpointNumber}");
        return signers;
    }

    /// <summary>
    /// Get candidate owner from validator contract (0x88) storage.
    /// Storage layout: validatorsState[candidate].owner at slot 1
    /// </summary>
    private Address GetCandidateOwner(Address candidate)
    {
        try
        {
            // Calculate storage slot: keccak256(candidate || slot)
            UInt256 locValidatorsState = GetLocMappingAtKey(candidate, ValidatorsStateSlot);
            // Owner is at offset 0 from the struct base
            UInt256 locOwner = locValidatorsState;

            var storageCell = new StorageCell(MasternodeVotingAddress, locOwner);
            ReadOnlySpan<byte> value = _worldState.Get(storageCell);

            if (value.Length == 0)
            {
                Console.WriteLine($"[XDC-REWARD] No owner found for {candidate} (empty storage)");
                return Address.Zero;
            }

            // Address is stored in last 20 bytes of 32-byte word
            ReadOnlySpan<byte> addressBytes = value.Length >= 32 
                ? value.Slice(12, 20) 
                : value.Slice(value.Length - 20, 20);

            Address owner = new Address(addressBytes.ToArray());
            Console.WriteLine($"[XDC-REWARD] Owner of {candidate}: {owner}");
            return owner;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XDC-REWARD] Error getting owner for {candidate}: {ex.Message}");
            return Address.Zero;
        }
    }

    /// <summary>
    /// Calculate storage slot for mapping[key].
    /// keccak256(key || slot)
    /// </summary>
    private UInt256 GetLocMappingAtKey(Address key, ulong slot)
    {
        Span<byte> input = stackalloc byte[64];
        
        // Key (address) left-padded to 32 bytes
        input.Slice(0, 12).Clear();
        key.Bytes.CopyTo(input.Slice(12, 20));
        
        // Slot as 32-byte big-endian
        input.Slice(32, 24).Clear();
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(56, 8), slot);

        ValueHash256 hash = ValueKeccak.Compute(input);
        return new UInt256(hash.Bytes, isBigEndian: true);
    }

    /// <summary>
    /// Aggregate rewards by address.
    /// </summary>
    private BlockReward[] AggregateRewards(List<BlockReward> rewards)
    {
        var aggregated = new Dictionary<Address, (UInt256 value, BlockRewardType type)>();
        
        foreach (var reward in rewards)
        {
            if (aggregated.TryGetValue(reward.Address, out var existing))
            {
                aggregated[reward.Address] = (existing.value + reward.Value, reward.RewardType);
            }
            else
            {
                aggregated[reward.Address] = (reward.Value, reward.RewardType);
            }
        }

        return aggregated.Select(kvp => 
            new BlockReward(kvp.Key, kvp.Value.value, kvp.Value.type)
        ).ToArray();
    }

    public IRewardCalculator Get(ITransactionProcessor processor) => this;
}

// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Xdc;

/// <summary>
/// XDPoS checkpoint reward calculator.
/// Ported from geth-xdc (eth/hooks/engine_v1_hooks.go) and erigon-xdc (consensus/xdpos/reward.go).
/// 
/// Rewards are distributed at every 900-block checkpoint:
///   - 90% to masternode owners (proportional to signing count)
///   - 0% to voters (on mainnet)
///   - 10% to foundation wallet
/// </summary>
public class XdcRewardCalculator : IRewardCalculator, IRewardCalculatorSource
{
    // === Constants from geth-xdc ===
    private const long RewardCheckpoint = 900;
    private const long RewardXdc = 250;  // 250 XDC per checkpoint (mainnet)
    private const int RewardMasterPercent = 90;
    private const int RewardVoterPercent = 0;
    private const int RewardFoundationPercent = 10;
    private const ulong BlocksPerYear = 15768000;
    private const ulong TIPNoHalvingMNReward = 38383838;
    private const int MergeSignRange = 15;
    
    private static readonly Address FoundationWallet = new("0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65");
    private static readonly Address BlockSignersContract = new("0x0000000000000000000000000000000000000089");
    private static readonly Address ValidatorContract = new("0x0000000000000000000000000000000000000088");

    private readonly IBlockTree? _blockTree;
    private readonly IWorldState? _stateProvider;
    private readonly ILogger _logger;

    public XdcRewardCalculator(ILogManager logManager, IBlockTree? blockTree = null, IWorldState? stateProvider = null)
    {
        _blockTree = blockTree;
        _stateProvider = stateProvider;
        _logger = logManager.GetClassLogger();
    }

    public IRewardCalculator Get(ITransactionProcessor processor) => this;

    public BlockReward[] CalculateRewards(Block block)
    {
        long number = block.Number;
        
        // Only at checkpoint blocks, and need at least 1 full epoch of history
        // Geth-xdc: number > 0 && number % rCheckpoint == 0 && number - rCheckpoint > 0
        if (number == 0 || number % RewardCheckpoint != 0 || number - RewardCheckpoint <= 0)
        {
            return Array.Empty<BlockReward>();
        }

        try
        {
            return CalculateCheckpointRewards(block);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XDC-REWARD] Block {number}: Error calculating rewards: {ex.Message}");
            if (_logger.IsError) _logger.Error($"Block {number}: Error calculating checkpoint rewards", ex);
            return Array.Empty<BlockReward>();
        }
    }

    private BlockReward[] CalculateCheckpointRewards(Block block)
    {
        long number = block.Number;
        
        // Calculate chain reward with inflation
        UInt256 chainReward = RewardInflation((UInt256)RewardXdc * Unit.Ether, (ulong)number);
        
        Console.WriteLine($"[XDC-REWARD] Block {number}: chainReward={chainReward} wei ({RewardXdc} XDC)");

        // Get signers and their sign counts from the previous epoch
        // prevCheckpoint = number - (rCheckpoint * 2)
        // startBlock = prevCheckpoint + 1
        // endBlock = startBlock + rCheckpoint - 1
        long prevCheckpoint = number - (RewardCheckpoint * 2);
        long startBlock = prevCheckpoint + 1;
        long endBlock = startBlock + RewardCheckpoint - 1;

        Console.WriteLine($"[XDC-REWARD] Block {number}: counting signers from blocks {startBlock} to {endBlock}");

        // Count signers from signing transactions in past blocks
        var signerCounts = CountSignersFromBlocks(startBlock, endBlock);
        
        if (signerCounts.Count == 0)
        {
            Console.WriteLine($"[XDC-REWARD] Block {number}: no signers found, skipping rewards");
            return Array.Empty<BlockReward>();
        }

        ulong totalSignCount = 0;
        foreach (var count in signerCounts.Values)
            totalSignCount += count;

        Console.WriteLine($"[XDC-REWARD] Block {number}: found {signerCounts.Count} signers, totalSignCount={totalSignCount}");

        // Calculate per-signer rewards and distribute to owners/foundation
        var rewards = new List<BlockReward>();
        
        foreach (var (signer, signCount) in signerCounts)
        {
            // signerReward = chainReward / totalSignCount * signCount
            UInt256 signerReward = chainReward / (UInt256)totalSignCount * (UInt256)signCount;
            
            // Get owner from validator contract (0x88)
            Address owner = GetCandidateOwner(signer);
            if (owner == Address.Zero)
                owner = signer;

            // 90% to owner
            UInt256 masterReward = signerReward * (UInt256)RewardMasterPercent / 100;
            if (masterReward > UInt256.Zero)
            {
                rewards.Add(new BlockReward(owner, masterReward, BlockRewardType.Block));
            }

            // 10% to foundation (per signer)
            UInt256 foundationReward = signerReward * (UInt256)RewardFoundationPercent / 100;
            if (foundationReward > UInt256.Zero)
            {
                rewards.Add(new BlockReward(FoundationWallet, foundationReward, BlockRewardType.External));
            }

            if (number == 1800)
                Console.WriteLine($"[XDC-REWARD]   signer={signer} signs={signCount} owner={owner} masterReward={masterReward} foundationReward={foundationReward}");
        }

        Console.WriteLine($"[XDC-REWARD] Block {number}: distributing {rewards.Count} rewards");
        return rewards.ToArray();
    }

    /// <summary>
    /// Count how many times each masternode signed blocks in the given range.
    /// Reads signing transactions from BlockSigners contract (0x89).
    /// In geth-xdc, signing txs are transactions TO 0x89 with the signer as tx.From.
    /// </summary>
    private Dictionary<Address, ulong> CountSignersFromBlocks(long startBlock, long endBlock)
    {
        var signerCounts = new Dictionary<Address, ulong>();
        
        if (_blockTree is null)
        {
            Console.WriteLine("[XDC-REWARD] No block tree available, using masternode list from state");
            return CountSignersFromState();
        }

        // Get masternodes from previous checkpoint header
        // For geth-xdc, masternodes = getMasternodesFromCheckpointHeader(prevCheckpointHeader)
        // We'll use the candidates from the 0x88 contract instead
        var masternodes = new HashSet<Address>(GetCandidatesFromState());
        
        for (long blockNum = startBlock; blockNum <= endBlock; blockNum++)
        {
            // In geth-xdc: only count at MergeSignRange intervals OR if block < TIP2019
            // TIP2019Block = 1 on mainnet, so ALL blocks qualify
            // For blocks >= TIP2019: only every 15th block
            bool shouldCount = blockNum < 1 || blockNum % MergeSignRange == 0;
            // TIP2019 = 1, so for mainnet all blocks >= 1 use MergeSignRange filter
            // But actually TIP2019Block=1 means IsTIP2019 is true for all blocks >= 1
            // Geth code: if i%common.MergeSignRange == 0 || !chain.Config().IsTIP2019(big.NewInt(int64(i)))
            // Since IsTIP2019 is true for all blocks >= 1, the !IsTIP2019 is false
            // So only blocks where blockNum % 15 == 0 are counted
            shouldCount = blockNum % MergeSignRange == 0;
            
            if (!shouldCount) continue;
            
            Block? pastBlock = _blockTree.FindBlock(blockNum, BlockTreeLookupOptions.None);
            if (pastBlock is null) continue;

            // Each tx TO 0x89 is a signing transaction
            foreach (var tx in pastBlock.Transactions)
            {
                if (tx.To is not null && tx.To == BlockSignersContract && tx.SenderAddress is not null)
                {
                    Address signer = tx.SenderAddress;
                    
                    // Only count if signer is a masternode
                    if (masternodes.Count == 0 || masternodes.Contains(signer))
                    {
                        signerCounts.TryGetValue(signer, out ulong count);
                        signerCounts[signer] = count + 1;
                    }
                }
            }
        }

        return signerCounts;
    }

    /// <summary>
    /// Fallback: get signers from state when block tree is not available.
    /// Uses the candidates list from 0x88 contract and assigns equal sign counts.
    /// </summary>
    private Dictionary<Address, ulong> CountSignersFromState()
    {
        var result = new Dictionary<Address, ulong>();
        var candidates = GetCandidatesFromState();
        foreach (var addr in candidates)
        {
            result[addr] = 1; // Equal distribution as fallback
        }
        return result;
    }

    /// <summary>
    /// Get candidate addresses from the Validator contract (0x88) storage.
    /// Slot 8 = "candidates" dynamic array.
    /// </summary>
    private Address[] GetCandidatesFromState()
    {
        if (_stateProvider is null) return Array.Empty<Address>();
        
        try
        {
            // slot 8 = candidates array
            var slotHash = new UInt256(8);
            var arrLengthStorage = _stateProvider.Get(new StorageCell(ValidatorContract, slotHash));
            if (arrLengthStorage.Length == 0) return Array.Empty<Address>();
            
            ulong count = new UInt256(arrLengthStorage, true).IsZero ? 0 : (ulong)new UInt256(arrLengthStorage, true);
            if (count == 0 || count > 1000) return Array.Empty<Address>();

            var candidates = new List<Address>();
            // Array elements at keccak256(slot) + index
            byte[] slotBytes = new byte[32];
            slotHash.ToBigEndian(slotBytes);
            var baseSlot = Nethermind.Core.Crypto.KeccakHash.ComputeHashBytes(slotBytes);

            for (ulong i = 0; i < count; i++)
            {
                var elementSlot = new UInt256(baseSlot, true) + new UInt256(i);
                var value = _stateProvider.Get(new StorageCell(ValidatorContract, elementSlot));
                if (value.Length > 0)
                {
                    var addr = new Address(value.Slice(value.Length >= 20 ? value.Length - 20 : 0, 20).ToArray());
                    if (addr != Address.Zero)
                        candidates.Add(addr);
                }
            }
            
            return candidates.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XDC-REWARD] Error reading candidates from state: {ex.Message}");
            return Array.Empty<Address>();
        }
    }

    /// <summary>
    /// Get the owner of a masternode candidate from 0x88 contract storage.
    /// Storage layout: validatorsState[candidate].owner at slot keccak256(candidate, 1) + 0
    /// </summary>
    private Address GetCandidateOwner(Address candidate)
    {
        if (_stateProvider is null) return Address.Zero;
        
        try
        {
            // slot 1 = validatorsState mapping
            byte[] candidateHash = new byte[32];
            candidate.Bytes.CopyTo(candidateHash.AsSpan(12)); // left-pad with zeros
            byte[] slotBytes = new byte[32];
            new UInt256(1).ToBigEndian(slotBytes);
            
            // keccak256(candidate_hash || slot)
            byte[] combined = new byte[64];
            candidateHash.CopyTo(combined, 0);
            slotBytes.CopyTo(combined, 32);
            var locValidatorsState = Nethermind.Core.Crypto.KeccakHash.ComputeHashBytes(combined);
            
            // owner is at offset 0
            var ownerSlot = new UInt256(locValidatorsState, true);
            var ownerBytes = _stateProvider.Get(new StorageCell(ValidatorContract, ownerSlot));
            if (ownerBytes.Length > 0)
            {
                var owner = new Address(ownerBytes.Slice(ownerBytes.Length >= 20 ? ownerBytes.Length - 20 : 0, 20).ToArray());
                return owner;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XDC-REWARD] Error getting owner for {candidate}: {ex.Message}");
        }
        
        return Address.Zero;
    }

    /// <summary>
    /// Apply reward halving based on block number.
    /// Years 2-5: halved, Years 5+: quartered.
    /// After TIPNoHalvingMNReward: no halving.
    /// </summary>
    private static UInt256 RewardInflation(UInt256 chainReward, ulong number)
    {
        if (number >= TIPNoHalvingMNReward)
            return chainReward;
            
        if (BlocksPerYear * 2 <= number && number < BlocksPerYear * 5)
            return chainReward / 2;
        
        if (number >= BlocksPerYear * 5)
            return chainReward / 4;
        
        return chainReward;
    }
}

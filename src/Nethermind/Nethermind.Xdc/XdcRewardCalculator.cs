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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

/// <summary>
/// XDPoS checkpoint reward calculator
/// 
/// XDPoS applies rewards ONLY at checkpoint blocks (every 900 blocks for V1),
/// NOT on every block. Rewards are distributed based on signing activity in
/// the previous epoch.
/// 
/// Algorithm from geth-xdc:
/// 1. At block N where N % 900 == 0 and N - 900 > 0 (so NOT at block 900)
/// 2. Calculate chain reward with inflation halving
/// 3. Get signers from previous epoch (blocks N-1800+1 to N-900)
/// 4. Count each masternode's signing activity
/// 5. Distribute rewards: 90% to masternode owners, 10% to foundation
/// 
/// Foundation wallet: 0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65
/// </summary>
public class XdcRewardCalculator : IRewardCalculator, IRewardCalculatorSource
{
    // Mainnet foundation wallet address (hardcoded from geth-xdc config)
    private static readonly Address MainnetFoundationWallet = 
        new("0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65");
    
    // Validator contract address (MasternodeVotingSMC)
    private static readonly Address ValidatorContractAddress = 
        new("0x0000000000000000000000000000000000000088");
    
    // BlockSigners contract address
    private static readonly Address BlockSignersAddress = 
        new("0x0000000000000000000000000000000000000089");
    
    // Checkpoint interval (V1: every 900 blocks)
    private const long RewardCheckpoint = 900;
    
    // Base block reward in XDC (from geth-xdc params/config.go)
    private const long BaseRewardXdc = 250;
    
    // Blocks per year for inflation calculation
    private const long BlocksPerYear = 15768000;
    
    // Fork block for no halving
    private const long TIPNoHalvingMNReward = 38383838;
    
    // Fork block for TIP2019 (used for MergeSignRange filter)
    private const long TIP2019Block = 1;
    
    // Fork block for TIPSigning (affects how signers are retrieved)
    private const long TIPSigningBlock = 3000000;
    
    // MergeSignRange - only count signatures every N blocks
    private const long MergeSignRange = 15;
    
    // Reward distribution percentages
    private const int RewardMasterPercent = 90;
    private const int RewardVoterPercent = 0;
    private const int RewardFoundationPercent = 10;
    
    // Storage slot positions
    private const ulong ValidatorsStateSlot = 1;
    private const ulong BlockSignersSlot = 0;

    private readonly XdcChainSpecEngineParameters _parameters;
    private readonly IBlockTree? _blockTree;
    private readonly ILogger _logger;
    
    private UInt256 _cachedChainReward;
    private long _cachedChainRewardBlock;

    public XdcRewardCalculator(
        ILogManager logManager,
        IBlockTree blockTree)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _logger = logManager?.GetClassLogger() ?? Nethermind.Logging.NullLogger.Instance;
        
        // Get parameters from chain spec - for now use defaults
        // In a full implementation, this would be injected properly
        _parameters = new XdcChainSpecEngineParameters
        {
            FoundationWalletAddr = MainnetFoundationWallet,
            Reward = (int)BaseRewardXdc
        };
        _cachedChainRewardBlock = -1;
    }
    
    /// <summary>
    /// Constructor with explicit parameters for testing.
    /// </summary>
    public XdcRewardCalculator(
        XdcChainSpecEngineParameters parameters, 
        IBlockTree? blockTree = null,
        ILogManager? logManager = null)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _blockTree = blockTree;
        _logger = logManager?.GetClassLogger() ?? Nethermind.Logging.NullLogger.Instance;
        _cachedChainRewardBlock = -1;
    }

    public BlockReward[] CalculateRewards(Block block)
    {
        // XDPoS does NOT apply rewards on every block
        // Rewards only at checkpoint blocks (every 900 for V1)
        // Geth-xdc condition: number > 0 && number - rCheckpoint > 0
        // This means block 900 gets NO rewards (900-900=0, not > 0)
        // First actual reward is at block 1800
        if (block.IsGenesis || block.Number % RewardCheckpoint != 0 
            || block.Number <= RewardCheckpoint)
        {
            return Array.Empty<BlockReward>();
        }

        // We need block tree access to read past blocks
        if (_blockTree is null)
        {
            _logger.Warn("BlockTree not available, cannot calculate checkpoint rewards");
            return Array.Empty<BlockReward>();
        }

        try
        {
            Console.WriteLine($"[XDC-REWARD] Calculating rewards for checkpoint block {block.Number}");
            _logger.Info($"Calculating rewards for checkpoint block {block.Number}");
            
            // Calculate chain reward with inflation
            UInt256 chainReward = CalculateChainReward((ulong)block.Number);
            Console.WriteLine($"[XDC-REWARD] Chain reward: {chainReward} wei ({chainReward / Unit.Ether} XDC)");
            
            // Get signers from previous epoch
            var (signerCounts, totalSignCount) = GetSignerCounts(block);
            
            if (totalSignCount == 0)
            {
                _logger.Warn($"No signers found for checkpoint block {block.Number}");
                Console.WriteLine($"[XDC-REWARD] No signers found, returning empty rewards");
                return Array.Empty<BlockReward>();
            }
            
            Console.WriteLine($"[XDC-REWARD] Found {signerCounts.Count} unique signers with {totalSignCount} total signs");
            
            // Calculate rewards
            var rewards = CalculateSignerRewards(chainReward, signerCounts, totalSignCount, block);
            
            Console.WriteLine($"[XDC-REWARD] Total rewards to distribute: {rewards.Length}");
            for (int i = 0; i < rewards.Length && i < 10; i++)
            {
                Console.WriteLine($"[XDC-REWARD]   {rewards[i].Address}: {rewards[i].Value / Unit.Ether} XDC ({rewards[i].RewardType})");
            }
            if (rewards.Length > 10)
            {
                Console.WriteLine($"[XDC-REWARD]   ... and {rewards.Length - 10} more");
            }
            
            return rewards;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error calculating rewards for block {block.Number}: {ex}");
            Console.WriteLine($"[XDC-REWARD] ERROR: {ex.Message}\n{ex.StackTrace}");
            return Array.Empty<BlockReward>();
        }
    }

    /// <summary>
    /// Calculate chain reward with inflation halving.
    /// Based on geth-xdc: eth/util/util.go RewardInflation
    /// </summary>
    private UInt256 CalculateChainReward(ulong blockNumber)
    {
        // Cache chain reward for same block
        if (_cachedChainRewardBlock == (long)blockNumber)
        {
            return _cachedChainReward;
        }
        
        // Base reward in wei
        UInt256 chainReward = (UInt256)BaseRewardXdc * Unit.Ether;
        
        // Apply halving if before TIPNoHalvingMNReward
        if (blockNumber < (ulong)TIPNoHalvingMNReward)
        {
            ulong twoYears = (ulong)BlocksPerYear * 2;
            ulong fiveYears = (ulong)BlocksPerYear * 5;
            
            if (blockNumber >= twoYears && blockNumber < fiveYears)
            {
                // Half reward
                chainReward /= 2;
            }
            else if (blockNumber >= fiveYears)
            {
                // Quarter reward
                chainReward /= 4;
            }
        }
        
        _cachedChainReward = chainReward;
        _cachedChainRewardBlock = (long)blockNumber;
        
        return chainReward;
    }

    /// <summary>
    /// Get signer counts from the previous epoch.
    /// Based on geth-xdc: contracts/utils.go GetRewardForCheckpoint
    /// </summary>
    private (Dictionary<Address, ulong> signerCounts, ulong totalSignCount) GetSignerCounts(Block checkpointBlock)
    {
        var signerCounts = new Dictionary<Address, ulong>();
        ulong totalSignCount = 0;
        
        ulong number = (ulong)checkpointBlock.Number;
        ulong prevCheckpoint = number - ((ulong)RewardCheckpoint * 2);
        ulong startBlockNumber = prevCheckpoint + 1;
        ulong endBlockNumber = startBlockNumber + (ulong)RewardCheckpoint - 1;
        
        Console.WriteLine($"[XDC-REWARD] Getting signers from epoch: {startBlockNumber} to {endBlockNumber}");
        _logger.Debug($"Getting signers from epoch: {startBlockNumber} to {endBlockNumber}");
        
        // Get masternodes from the previous checkpoint header
        // These are the valid signers we should count
        Address[] masternodes = GetMasternodesFromCheckpoint(prevCheckpoint);
        if (masternodes.Length == 0)
        {
            _logger.Warn($"No masternodes found at checkpoint {prevCheckpoint}");
            Console.WriteLine($"[XDC-REWARD] No masternodes found at checkpoint {prevCheckpoint}");
            return (signerCounts, totalSignCount);
        }
        
        Console.WriteLine($"[XDC-REWARD] Found {masternodes.Length} masternodes at checkpoint {prevCheckpoint}");
        _logger.Debug($"Found {masternodes.Length} masternodes");
        
        var masternodeSet = new HashSet<Address>(masternodes);
        
        // For each block in the epoch, get signers
        for (ulong blockNum = startBlockNumber; blockNum <= endBlockNumber; blockNum++)
        {
            // Apply MergeSignRange filter: only count every 15 blocks OR before TIP2019
            // Since TIP2019 = 1 for mainnet, effectively always true for early blocks
            if (blockNum % (ulong)MergeSignRange != 0 && blockNum >= (ulong)TIP2019Block)
            {
                continue;
            }
            
            // Get signers for this block
            Address[] blockSigners = GetBlockSigners(blockNum);
            
            // Filter to only valid masternodes and count unique signers per block
            var seenInBlock = new HashSet<Address>();
            foreach (var signer in blockSigners)
            {
                if (masternodeSet.Contains(signer) && !seenInBlock.Contains(signer))
                {
                    seenInBlock.Add(signer);
                    
                    if (!signerCounts.ContainsKey(signer))
                    {
                        signerCounts[signer] = 0;
                    }
                    signerCounts[signer]++;
                    totalSignCount++;
                }
            }
        }
        
        Console.WriteLine($"[XDC-REWARD] Signer counts: {signerCounts.Count} signers, {totalSignCount} total signs");
        foreach (var kvp in signerCounts)
        {
            Console.WriteLine($"[XDC-REWARD]   {kvp.Key}: {kvp.Value} signs");
        }
        
        return (signerCounts, totalSignCount);
    }

    /// <summary>
    /// Get masternodes from a checkpoint block header.
    /// </summary>
    private Address[] GetMasternodesFromCheckpoint(ulong checkpointNumber)
    {
        // Find the block at the checkpoint
        BlockHeader? header = _blockTree?.FindHeader((long)checkpointNumber);
        if (header is null)
        {
            _logger.Warn($"Could not find header for checkpoint {checkpointNumber}");
            return Array.Empty<Address>();
        }
        
        // For XDC, masternodes are stored in the header's ExtraData
        // V1 format: [32 bytes vanity][N*20 bytes signers][65 bytes seal]
        return ExtractMasternodesFromHeader(header);
    }

    /// <summary>
    /// Extract masternodes from checkpoint block header extra data.
    /// V1 format: [32 bytes vanity][N*20 bytes signers][65 bytes seal]
    /// </summary>
    private Address[] ExtractMasternodesFromHeader(BlockHeader header)
    {
        byte[]? extraData = header.ExtraData;
        if (extraData is null || extraData.Length < 32 + 65)
        {
            return Array.Empty<Address>();
        }
        
        // V1: signers are between vanity (32 bytes) and seal (65 bytes)
        int signersDataLength = extraData.Length - 32 - 65;
        if (signersDataLength <= 0 || signersDataLength % 20 != 0)
        {
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
        
        return signers;
    }

    /// <summary>
    /// Get signers for a specific block from BlockSigners contract.
    /// </summary>
    private Address[] GetBlockSigners(ulong blockNumber)
    {
        // Get the block header
        BlockHeader? header = _blockTree?.FindHeader((long)blockNumber);
        if (header is null)
        {
            return Array.Empty<Address>();
        }
        
        // Get the block to access transactions
        Block? block = _blockTree?.FindBlock(header.Hash!);
        if (block is null)
        {
            return Array.Empty<Address>();
        }
        
        var signers = new List<Address>();
        
        // For pre-TIPSigning blocks (before 3,000,000), we read signing transactions
        // from the block's transaction list
        if (blockNumber < (ulong)TIPSigningBlock)
        {
            foreach (var tx in block.Transactions)
            {
                // Signing transactions are sent TO the BlockSigners contract (0x89)
                if (tx.To == BlockSignersAddress && tx.Data.Length >= 32)
                {
                    // The sender is the signer
                    if (tx.SenderAddress is not null)
                    {
                        signers.Add(tx.SenderAddress);
                    }
                }
            }
        }
        else
        {
            // Post-TIPSigning: signers are stored in contract storage
            // This would require reading from the parent state
            // For now, use transaction-based approach as fallback
            foreach (var tx in block.Transactions)
            {
                if (tx.To == BlockSignersAddress && tx.SenderAddress is not null)
                {
                    signers.Add(tx.SenderAddress);
                }
            }
        }
        
        return signers.ToArray();
    }

    /// <summary>
    /// Calculate rewards for each signer and distribute to owners and foundation.
    /// Based on geth-xdc: contracts/utils.go CalculateRewardForSigner and CalculateRewardForHolders
    /// </summary>
    private BlockReward[] CalculateSignerRewards(
        UInt256 chainReward, 
        Dictionary<Address, ulong> signerCounts, 
        ulong totalSignCount,
        Block checkpointBlock)
    {
        var rewards = new List<BlockReward>();
        var foundationWallet = GetFoundationWallet();
        
        // Calculate rewards for each signer
        foreach (var (signer, signCount) in signerCounts)
        {
            // Calculate this signer's reward portion
            // signerReward = chainReward / totalSignCount * signerSignCount
            UInt256 signerReward = chainReward / totalSignCount * signCount;
            
            // Get the owner of this masternode from validator contract
            Address owner = GetCandidateOwner(signer, checkpointBlock);
            
            if (owner == Address.Zero)
            {
                // If no owner found, use the signer as owner
                owner = signer;
            }
            
            // Calculate reward splits
            // Master (owner) gets 90%
            UInt256 masterReward = signerReward * (ulong)RewardMasterPercent / 100;
            
            // Foundation gets 10%
            UInt256 foundationReward = signerReward * (ulong)RewardFoundationPercent / 100;
            
            // Add owner reward
            rewards.Add(new BlockReward(owner, masterReward, BlockRewardType.Block));
            
            // Add foundation reward (we'll aggregate these below)
            rewards.Add(new BlockReward(foundationWallet, foundationReward, BlockRewardType.External));
            
            Console.WriteLine($"[XDC-REWARD] Signer {signer}: signs={signCount}, total={signerReward / Unit.Ether} XDC, owner={owner}, master={masterReward / Unit.Ether}, foundation={foundationReward / Unit.Ether}");
        }
        
        // Aggregate foundation rewards
        var aggregatedRewards = new Dictionary<Address, UInt256>();
        foreach (var reward in rewards)
        {
            if (!aggregatedRewards.ContainsKey(reward.Address))
            {
                aggregatedRewards[reward.Address] = UInt256.Zero;
            }
            aggregatedRewards[reward.Address] += reward.Value;
        }
        
        // Convert to BlockReward array
        return aggregatedRewards.Select(kvp => 
            new BlockReward(kvp.Key, kvp.Value, kvp.Key == foundationWallet ? BlockRewardType.External : BlockRewardType.Block)
        ).ToArray();
    }

    /// <summary>
    /// Get foundation wallet address from config or use default.
    /// </summary>
    private Address GetFoundationWallet()
    {
        if (!string.IsNullOrEmpty(_parameters.FoundationWalletAddr?.ToString()) 
            && _parameters.FoundationWalletAddr != Address.Zero)
        {
            return _parameters.FoundationWalletAddr;
        }
        return MainnetFoundationWallet;
    }

    /// <summary>
    /// Get candidate owner from validator contract storage.
    /// Based on geth-xdc: core/state/statedb_utils.go GetCandidateOwner
    /// </summary>
    private Address GetCandidateOwner(Address signer, Block checkpointBlock)
    {
        try
        {
            // Compute the storage slot for validatorsState[signer].owner
            // locValidatorsState = keccak256(signer || slot)
            // locCandidateOwner = locValidatorsState + 0 (owner is first field)
            
            UInt256 locValidatorsState = GetLocMappingAtKey(signer, ValidatorsStateSlot);
            UInt256 locCandidateOwner = locValidatorsState;  // + 0 for .owner field

            // Read storage at contract 0x88
            // We need to read from the parent state (state at checkpoint-1)
            // For simplicity, we'll use the checkpoint block's state if available
            // In a full implementation, we'd need access to IWorldState at the parent block
            
            // Since we don't have direct state access here, return zero
            // The actual owner lookup would require state access at parent block
            // This is a simplified implementation
            
            return Address.Zero;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Error getting candidate owner for {signer}: {ex.Message}");
            return Address.Zero;
        }
    }

    /// <summary>
    /// Computes the storage slot for a mapping key.
    /// For mapping(key => value) at slot s, the storage location is:
    /// keccak256(abi.encode(key, s))
    /// </summary>
    private UInt256 GetLocMappingAtKey(Address key, ulong slot)
    {
        // Create the input for keccak256: key (32 bytes, left-padded) || slot (32 bytes)
        Span<byte> input = stackalloc byte[64];
        
        // Key is 20 bytes, left-pad with zeros to 32 bytes
        input.Slice(0, 12).Clear();  // First 12 bytes = 0
        key.Bytes.CopyTo(input.Slice(12, 20));  // Next 20 bytes = address
        
        // Slot is uint64, encode as 32-byte big-endian
        input.Slice(32, 24).Clear();  // First 24 bytes = 0
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(56, 8), slot);

        // Compute keccak256
        ValueHash256 hash = ValueKeccak.Compute(input);
        
        // Convert hash bytes to UInt256
        return new UInt256(hash.Bytes, isBigEndian: true);
    }

    public IRewardCalculator Get(ITransactionProcessor processor) => this;
}

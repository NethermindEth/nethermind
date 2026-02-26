// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Crypto;

namespace Nethermind.Xdc;

/// <summary>
/// XDPoS checkpoint reward calculator.
/// Ported from geth-xdc (eth/hooks/engine_v1_hooks.go, contracts/utils.go).
/// 
/// At every 900-block checkpoint (except block 900):
/// 1. Count signing transactions from previous epoch (blocks to 0x89)
/// 2. Only count qualifying blocks (block % MergeSignRange == 0, since TIP2019 active)
/// 3. Distribute chainReward proportionally to sign count
/// 4. For each signer: 90% to owner (from 0x88), 10% to foundation
/// </summary>
public class XdcRewardCalculator : IRewardCalculator, IRewardCalculatorSource
{
    private const long RewardCheckpoint = 900;
    private const int MergeSignRange = 15;
    private const ulong BlocksPerYear = 15768000;
    private const ulong TIPNoHalvingMNReward = 38383838;
    private const int RewardMasterPercent = 90;
    private const int RewardFoundationPercent = 10;
    
    private static readonly Address FoundationWallet = new("0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65");
    private static readonly Address BlockSignersContract = new("0x0000000000000000000000000000000000000089");
    private static readonly Address ValidatorContract = new("0x0000000000000000000000000000000000000088");

    private readonly IBlockTree? _blockTree;
    private readonly IWorldState? _stateProvider;
    private readonly IEthereumEcdsa? _ecdsa;
    private readonly ILogger _logger;

    public XdcRewardCalculator(ILogManager logManager, IBlockTree? blockTree = null, IWorldState? stateProvider = null, IEthereumEcdsa? ecdsa = null)
    {
        _blockTree = blockTree;
        _stateProvider = stateProvider;
        _ecdsa = ecdsa;
        _logger = logManager?.GetClassLogger() ?? NullLogger.Instance;
    }

    public IRewardCalculator Get(ITransactionProcessor processor) => this;

    public BlockReward[] CalculateRewards(Block block)
    {
        long number = block.Number;
        
        // Geth condition: number > 0 && number - rCheckpoint > 0
        if (block.IsGenesis || number % RewardCheckpoint != 0 || number <= RewardCheckpoint)
            return Array.Empty<BlockReward>();

        try
        {
            return CalculateCheckpointRewards(block);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error calculating rewards for block {number}", ex);
            return Array.Empty<BlockReward>();
        }
    }

    private BlockReward[] CalculateCheckpointRewards(Block block)
    {
        long number = block.Number;
        // Console.WriteLine($"[XDC-REWARD] ====== Block {number} Checkpoint Rewards ======");
        
        // 1. Calculate chain reward with inflation
        UInt256 chainReward = RewardInflation((UInt256)250 * 1_000_000_000_000_000_000, (ulong)number);
        // Console.WriteLine($"[XDC-REWARD] chainReward = {chainReward}");

        // 2. Get masternodes from prev checkpoint header
        long prevCheckpoint = number - RewardCheckpoint;
        Address[] masternodes = GetMasternodesFromCheckpoint(prevCheckpoint);
        if (masternodes.Length == 0)
        {
            // Console.WriteLine($"[XDC-REWARD] No masternodes found, skipping");
            return Array.Empty<BlockReward>();
        }
        var masternodeSet = new HashSet<Address>(masternodes);

        // 3. Count signing transactions (geth GetRewardForCheckpoint logic)
        // prevCheckpoint2 = number - (rCheckpoint * 2) 
        long prevCheckpoint2 = number - (RewardCheckpoint * 2);
        long startBlock = prevCheckpoint2 + 1;
        long endBlock = startBlock + RewardCheckpoint - 1;
        
        // Console.WriteLine($"[XDC-REWARD] Counting signers for blocks {startBlock}-{endBlock}");

        // First: collect all signing txs and map blockHash -> signers
        // Geth iterates from (prevCheckpoint + rCheckpoint*2 - 1) down to startBlock
        var blockHashSigners = new Dictionary<Hash256, List<Address>>();
        var blockNumToHash = new Dictionary<long, Hash256>();
        
        if (_blockTree is null)
        {
            // Console.WriteLine($"[XDC-REWARD] No block tree! Cannot count signing txs");
            return Array.Empty<BlockReward>();
        }

        // Iterate backwards collecting signing txs (matching geth order)
        long blocksFound = 0;
        long txCount = 0;
        long signingTxCount = 0;
        for (long i = prevCheckpoint2 + (RewardCheckpoint * 2) - 1; i >= startBlock; i--)
        {
            Block? pastBlock = _blockTree.FindBlock(i, BlockTreeLookupOptions.None);
            if (pastBlock is null) continue;
            blocksFound++;
            blockNumToHash[i] = pastBlock.Hash!;
            txCount += pastBlock.Transactions.Length;
            
            foreach (var tx in pastBlock.Transactions)
            {
                var to = tx.To?.ToString()?.ToLowerInvariant();
                if (to == BlockSignersContract.ToString().ToLowerInvariant())
                {
                    // Recover sender if not available
                    Address? sender = tx.SenderAddress;
                    if (sender is null && _ecdsa is not null)
                    {
                        try { sender = _ecdsa.RecoverAddress(tx); }
                        catch { sender = null; }
                    }
                    if (sender is null) continue;
                    
                    signingTxCount++;
                    // Console.WriteLine($"[XDC-REWARD] Found signing tx in block {pastBlock.Number} from {sender} to {to}");
                    
                    byte[] txData = tx.Data.ToArray();
                    if (txData.Length >= 32)
                    {
                        byte[] hashBytes = new byte[32];
                        Array.Copy(txData, txData.Length - 32, hashBytes, 0, 32);
                        var signedBlockHash = new Hash256(hashBytes);
                        
                        if (!blockHashSigners.ContainsKey(signedBlockHash))
                            blockHashSigners[signedBlockHash] = new List<Address>();
                        blockHashSigners[signedBlockHash].Add(sender);
                    }
                }
            }
        }

        // Also get hash for startBlock-1 if needed (prevCheckpoint2 header)
        var prevHeader = _blockTree.FindBlock(prevCheckpoint2, BlockTreeLookupOptions.None);
        if (prevHeader is not null)
            blockNumToHash[prevCheckpoint2] = prevHeader.Hash!;

        // Console.WriteLine($"[XDC-REWARD] Found {blocksFound} blocks, {txCount} total txs, {signingTxCount} signing txs");
        // Console.WriteLine($"[XDC-REWARD] blockHashSigners has {blockHashSigners.Count} entries");

        // 4. Count signers per qualifying block (matching geth logic)
        var signerCounts = new Dictionary<Address, ulong>();
        ulong totalSigner = 0;

        long qualifyingBlocks = 0;
        long matchedBlocks = 0;
        for (long i = startBlock; i <= endBlock; i++)
        {
            // MergeSignRange filter: TIP2019 is active (block >= 1), so only i % 15 == 0
            if (i % MergeSignRange != 0) continue;
            qualifyingBlocks++;
            
            // Use locally stored block hash
            if (!blockNumToHash.TryGetValue(i, out Hash256? blkHash) || blkHash is null) continue;
            
            if (!blockHashSigners.TryGetValue(blkHash, out var addrs) || addrs.Count == 0)
            {
                if (i <= 30 || i == 900)
                    // Console.WriteLine($"[XDC-REWARD] Block {i} hash {blkHash} has NO signing tx matches");
                continue;
            }
            matchedBlocks++;
            
            // Filter: only masternodes, no duplicates
            var addrSigners = new HashSet<Address>();
            foreach (var mn in masternodes)
            {
                foreach (var addr in addrs)
                {
                    if (addr == mn)
                    {
                        addrSigners.Add(addr);
                        break;
                    }
                }
            }

            foreach (var addr in addrSigners)
            {
                signerCounts.TryGetValue(addr, out ulong count);
                signerCounts[addr] = count + 1;
                totalSigner++;
            }
        }

        // Console.WriteLine($"[XDC-REWARD] qualifyingBlocks={qualifyingBlocks}, matchedBlocks={matchedBlocks}");
        // Console.WriteLine($"[XDC-REWARD] totalSigner = {totalSigner}, unique signers = {signerCounts.Count}");
        foreach (var (addr, cnt) in signerCounts)
            // Console.WriteLine($"[XDC-REWARD]   {addr}: {cnt} signs");

        if (totalSigner == 0)
        {
            // Console.WriteLine($"[XDC-REWARD] No signers found, skipping rewards");
            return Array.Empty<BlockReward>();
        }

        // 5. Calculate per-signer rewards (geth CalculateRewardForSigner)
        // calcReward = (chainReward / totalSigner) * signCount
        var signerRewards = new Dictionary<Address, UInt256>();
        foreach (var (signer, signCount) in signerCounts)
        {
            UInt256 calcReward = (chainReward / (UInt256)totalSigner) * (UInt256)signCount;
            signerRewards[signer] = calcReward;
            // Console.WriteLine($"[XDC-REWARD]   {signer}: calcReward = {calcReward}");
        }

        // 6. Distribute to owners and foundation, matching geth order exactly:
        // For each signer: AddBalance(owner, 90%), AddBalance(foundation, 10%)
        var result = new List<BlockReward>();
        UInt256 total = UInt256.Zero;

        foreach (var (signer, calcReward) in signerRewards)
        {
            // Get owner from 0x88 contract (geth uses parentState)
            Address owner = GetCandidateOwner(signer);
            if (owner == Address.Zero) owner = signer;

            // 90% to owner
            UInt256 ownerReward = calcReward * (UInt256)RewardMasterPercent / 100;
            result.Add(new BlockReward(owner, ownerReward, BlockRewardType.Block));
            total += ownerReward;

            // 10% to foundation  
            UInt256 foundationReward = calcReward * (UInt256)RewardFoundationPercent / 100;
            result.Add(new BlockReward(FoundationWallet, foundationReward, BlockRewardType.External));
            total += foundationReward;

            // Console.WriteLine($"[XDC-REWARD]   {signer} -> owner {owner}: {ownerReward} wei, foundation: {foundationReward} wei");
        }

        // Console.WriteLine($"[XDC-REWARD] Total distributed: {total} wei ({result.Count} reward entries)");
        // Console.WriteLine($"[XDC-REWARD] ====== End Block {number} ======");

        return result.ToArray();
    }

    /// <summary>
    /// Get masternodes from checkpoint header ExtraData.
    /// V1 format: [32 vanity][N*20 masternode addrs][65 seal]
    /// </summary>
    private Address[] GetMasternodesFromCheckpoint(long checkpointBlock)
    {
        if (_blockTree is null) return Array.Empty<Address>();
        
        var header = _blockTree.FindHeader(checkpointBlock, BlockTreeLookupOptions.None);
        if (header is null) return Array.Empty<Address>();

        byte[] extra = header.ExtraData ?? Array.Empty<byte>();
        if (extra.Length < 32 + 65) return Array.Empty<Address>();

        int signersLength = extra.Length - 32 - 65;
        if (signersLength <= 0 || signersLength % 20 != 0) return Array.Empty<Address>();

        int count = signersLength / 20;
        var masternodes = new Address[count];
        for (int i = 0; i < count; i++)
        {
            byte[] addr = new byte[20];
            Array.Copy(extra, 32 + (i * 20), addr, 0, 20);
            masternodes[i] = new Address(addr);
        }
        
        // Console.WriteLine($"[XDC-REWARD] Extracted {count} masternodes from checkpoint {checkpointBlock}");
        return masternodes;
    }

    /// <summary>
    /// Get owner of masternode from Validator contract (0x88).
    /// Storage: validatorsState[candidate].owner at keccak256(candidate, slot=1) + 0
    /// </summary>
    private Address GetCandidateOwner(Address candidate)
    {
        if (_stateProvider is null) return Address.Zero;
        
        try
        {
            // Slot 1 = validatorsState mapping
            byte[] candidateHash = new byte[32];
            candidate.Bytes.CopyTo(candidateHash.AsSpan(12));
            byte[] slotBytes = new byte[32];
            new UInt256(1).ToBigEndian(slotBytes);
            
            byte[] combined = new byte[64];
            candidateHash.CopyTo(combined, 0);
            slotBytes.CopyTo(combined, 32);
            var locHash = Nethermind.Core.Crypto.KeccakHash.ComputeHashBytes(combined);
            
            var ownerSlot = new UInt256(locHash, true);
            var ownerBytes = _stateProvider.Get(new StorageCell(ValidatorContract, ownerSlot));
            if (ownerBytes.Length > 0)
            {
                var owner = new Address(ownerBytes.Slice(ownerBytes.Length >= 20 ? ownerBytes.Length - 20 : 0, 20).ToArray());
                return owner;
            }
        }
        catch (Exception)
        {
            // Console.WriteLine($"[XDC-REWARD] Error getting owner for {candidate}: {ex.Message}");
        }
        
        return Address.Zero;
    }

    private static UInt256 RewardInflation(UInt256 chainReward, ulong blockNumber)
    {
        if (blockNumber >= TIPNoHalvingMNReward) return chainReward;
        if (BlocksPerYear * 2 <= blockNumber && blockNumber < BlocksPerYear * 5) return chainReward / 2;
        if (blockNumber >= BlocksPerYear * 5) return chainReward / 4;
        return chainReward;
    }
}

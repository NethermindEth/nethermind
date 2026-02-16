// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

/// <summary>
/// XDPoS block reward calculator
/// 
/// XDPoS applies rewards ONLY at checkpoint blocks (every 900 blocks for V1,
/// at epoch switch blocks for V2), NOT on every block.
/// 
/// Reward distribution:
/// - Total: 5000 XDC per checkpoint
/// - 90% to masternodes (split equally among active masternodes)
/// - 10% to foundation wallet
/// 
/// Foundation wallet: 0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65
/// (Note: field name in geth-xdc is intentionally misspelled as "FoudationWalletAddr")
/// </summary>
public class XdcRewardCalculator : IRewardCalculator, IRewardCalculatorSource
{
    // Mainnet foundation wallet address (hardcoded from geth-xdc config)
    private static readonly Address MainnetFoundationWallet = 
        new("0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65");
    
    // Checkpoint interval (V1: every 900 blocks)
    private const long CheckpointInterval = 900;
    
    // Block reward in XDC (from geth-xdc params/config.go)
    private const long BlockRewardXdc = 5000;
    
    // Reward distribution percentages
    private const int MasternodePercent = 90;
    private const int FoundationPercent = 10;

    private readonly XdcChainSpecEngineParameters _parameters;
    private readonly UInt256 _blockReward;
    private readonly Address _foundationWallet;

    public XdcRewardCalculator(XdcChainSpecEngineParameters parameters)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        
        // Convert reward from XDC to Wei (1 XDC = 10^18 Wei)
        _blockReward = (UInt256)BlockRewardXdc * Unit.Ether;
        
        // Use configured foundation wallet or default to mainnet address
        _foundationWallet = !string.IsNullOrEmpty(parameters.FoundationWalletAddr?.ToString()) 
            && parameters.FoundationWalletAddr != Address.Zero
            ? parameters.FoundationWalletAddr 
            : MainnetFoundationWallet;
    }

    public BlockReward[] CalculateRewards(Block block)
    {
        // XDPoS does NOT apply rewards on every block
        // Rewards only at checkpoint blocks (every 900 for V1)
        if (block.IsGenesis || block.Number % CheckpointInterval != 0)
        {
            return Array.Empty<BlockReward>();
        }

        // Calculate reward splits
        UInt256 foundationReward = _blockReward * (UInt256)FoundationPercent / 100;
        UInt256 masternodeReward = _blockReward * (UInt256)MasternodePercent / 100;

        // TODO: Get actual masternode list from checkpoint block header
        // For now, return full masternode reward to block beneficiary
        // This needs to be enhanced to split among all active masternodes
        // based on header.Extra data (V1) or header.Validators (V2)
        
        // Get masternodes from checkpoint block
        Address[] masternodes = GetMasternodesFromCheckpoint(block.Header);
        
        if (masternodes.Length == 0)
        {
            // No masternodes found - give everything to block beneficiary as fallback
            return new[]
            {
                new BlockReward(block.Beneficiary, _blockReward, BlockRewardType.Block)
            };
        }

        // Split masternode reward equally among all masternodes
        UInt256 rewardPerMasternode = masternodeReward / (ulong)masternodes.Length;
        
        var rewards = new BlockReward[masternodes.Length + 1];
        
        // 1. Foundation reward (10%)
        rewards[0] = new BlockReward(_foundationWallet, foundationReward, BlockRewardType.External);
        
        // 2. Masternode rewards (90% split equally)
        for (int i = 0; i < masternodes.Length; i++)
        {
            rewards[i + 1] = new BlockReward(masternodes[i], rewardPerMasternode, BlockRewardType.Block);
        }

        return rewards;
    }

    /// <summary>
    /// Extract masternodes from checkpoint block header
    /// V1: Extract from ExtraData field (after vanity, before seal)
    /// V2: Extract from Validators field
    /// </summary>
    private Address[] GetMasternodesFromCheckpoint(BlockHeader header)
    {
        // TODO: Implement V1/V2 detection and proper extraction
        // For now, return empty to use fallback behavior
        
        // V1 extraction:
        // - ExtraData format: [32 bytes vanity][N*20 bytes signers][65 bytes seal]
        // - Signers are the masternode addresses
        
        // V2 extraction:
        // - Use header.Validators field directly
        
        // This requires implementing XdcHeaderDecoder.GetSigners() or similar
        return Array.Empty<Address>();
    }

    public IRewardCalculator Get(ITransactionProcessor processor) => this;
}

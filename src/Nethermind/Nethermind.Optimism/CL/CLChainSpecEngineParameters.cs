// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism.CL;

public class CLChainSpecEngineParameters : IChainSpecEngineParameters
{
    public Address? BatcherInboxAddress { get; set; }
    public ulong? L2BlockTime { get; set; }
    public ulong? SeqWindowSize { get; set; }
    public ulong? MaxSequencerDrift { get; set; }

    // roles
    public Address? SystemConfigOwner { get; set; }
    public Address? ProxyAdminOwner { get; set; }
    public Address? Guardian { get; set; }
    public Address? Challenger { get; set; }
    public Address? Proposer { get; set; }
    public Address? UnsafeBlockSigner { get; set; }
    public Address? BatchSubmitter { get; set; }

    // addresses
    public Address? AddressManager { get; set; }
    public Address? L1CrossDomainMessengerProxy { get; set; }
    public Address? L1ERC721BridgeProxy { get; set; }
    public Address? L1StandardBridgeProxy { get; set; }
    public Address? L2OutputOracleProxy { get; set; }
    public Address? OptimismMintableERC20FactoryProxy { get; set; }
    public Address? OptimismPortalProxy { get; set; }
    public Address? SystemConfigProxy { get; set; }
    public Address? ProxyAdmin { get; set; }
    public Address? SuperchainConfig { get; set; }
    public Address? AnchorStateRegistryProxy { get; set; }
    public Address? DelayedWETHProxy { get; set; }
    public Address? DisputeGameFactoryProxy { get; set; }
    public Address? MIPS { get; set; }
    public Address? PermissionedDisputeGame { get; set; }
    public Address? PreimageOracle { get; set; }

    public Address SystemTransactionSender { get; set; } = new("0xDeaDDEaDDeAdDeAdDEAdDEaddeAddEAdDEAd0001");
    public Address SystemTransactionTo { get; set; } = new("0x4200000000000000000000000000000000000015");
    public string[]? Nodes { get; set; }
    public ulong? L1BeaconGenesisSlotTime { get; set; }
    public string? EngineName => "OptimismCL";
    public string? SealEngineType => null;
}

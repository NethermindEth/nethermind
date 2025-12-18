// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism.CL;

public class CLChainSpecEngineParameters : IChainSpecEngineParameters
{
    public Address? BatcherInboxAddress { get; init; }
    public ulong? L2BlockTime { get; init; }
    public ulong? SeqWindowSize { get; init; }
    public ulong? MaxSequencerDrift { get; init; }
    public ulong? ChannelTimeoutBedrock { get; init; }

    // roles
    public Address? SystemConfigOwner { get; init; }
    public Address? ProxyAdminOwner { get; init; }
    public Address? Guardian { get; init; }
    public Address? Challenger { get; init; }
    public Address? Proposer { get; init; }
    public Address? UnsafeBlockSigner { get; init; }
    public Address? BatchSubmitter { get; init; }

    // addresses
    public Address? AddressManager { get; init; }
    public Address? L1CrossDomainMessengerProxy { get; init; }
    public Address? L1ERC721BridgeProxy { get; init; }
    public Address? L1StandardBridgeProxy { get; init; }
    public Address? L2OutputOracleProxy { get; init; }
    public Address? OptimismMintableERC20FactoryProxy { get; init; }
    public Address? OptimismPortalProxy { get; init; }
    public Address? SystemConfigProxy { get; init; }
    public Address? ProxyAdmin { get; init; }
    public Address? SuperchainConfig { get; init; }
    public Address? AnchorStateRegistryProxy { get; init; }
    public Address? DelayedWETHProxy { get; init; }
    public Address? DisputeGameFactoryProxy { get; init; }
    public Address? MIPS { get; init; }
    public Address? PermissionedDisputeGame { get; init; }
    public Address? PreimageOracle { get; init; }

    public Address SystemTransactionSender { get; init; } = new("0xDeaDDEaDDeAdDeAdDEAdDEaddeAddEAdDEAd0001");
    public Address SystemTransactionTo { get; init; } = new("0x4200000000000000000000000000000000000015");
    public string[]? Nodes { get; init; }
    public ulong? L1BeaconGenesisSlotTime { get; init; }
    public ulong? L1ChainId { get; init; }
    public ulong? L1GenesisNumber { get; init; }
    public Hash256? L1GenesisHash { get; init; }
    public OptimismSystemConfig? GenesisSystemConfig { get; init; }
    public string? EngineName => "OptimismCL";
    public string? SealEngineType => null;
}

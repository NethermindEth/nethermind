// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.AuRa.Config;

public interface IAuraConfig : IConfig
{
    [ConfigItem(Description = "Whether to seal empty blocks if mining.", DefaultValue = "true")]
    bool ForceSealing { get; set; }

    [ConfigItem(Description = "Whether to allow private Aura-based chains only. Do not use with existing Aura-based chains.", DefaultValue = "false")]
    bool AllowAuRaPrivateChains { get; set; }

    [ConfigItem(Description = "Whether to use 2M gas if the contract returns less than that when using `BlockGasLimitContractTransitions`.", DefaultValue = "false")]
    bool Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract { get; set; }

    [ConfigItem(Description = "The address of the transaction priority contract to use when selecting transactions from the transaction pool.",
        DefaultValue = "null")]
    string TxPriorityContractAddress { get; set; }

    [ConfigItem(Description = "The path to the transaction priority rules file to use when selecting transactions from the transaction pool.",
        DefaultValue = "null")]
    string TxPriorityConfigFilePath { get; set; }

    [ConfigItem(Description = "Whether to enable shuttering.", DefaultValue = "false")]
    bool UseShutter { get; set; }

    [ConfigItem(Description = "The address of the Shutter sequencer contract.",
        DefaultValue = "null")]
    string ShutterSequencerContractAddress { get; set; }

    [ConfigItem(Description = "The address of the Shutter validator registry contract.",
        DefaultValue = "null")]
    string ShutterValidatorRegistryContractAddress { get; set; }


    [ConfigItem(Description = "The address of the Shutter key broadcast contract.",
        DefaultValue = "null")]
    string ShutterKeyBroadcastContractAddress { get; set; }

    [ConfigItem(Description = "The address of the Shutter keyper set manager contract.",
        DefaultValue = "null")]
    string ShutterKeyperSetManagerContractAddress { get; set; }

    [ConfigItem(Description = "The p2p addresses of the Shutter keypers.",
        DefaultValue = "[]")]
    string[] ShutterKeyperP2PAddresses { get; set; }

    [ConfigItem(Description = "The port to connect to Shutter P2P network with.",
        DefaultValue = "23001")]
    int ShutterP2PPort { get; set; }
}

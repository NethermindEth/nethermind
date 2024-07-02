// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.AuRa.Config;

public interface IShutterConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable shuttering.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The address of the Shutter sequencer contract.",
        DefaultValue = "null")]
    string? SequencerContractAddress { get; set; }

    [ConfigItem(Description = "The address of the Shutter validator registry contract.",
        DefaultValue = "null")]
    string? ValidatorRegistryContractAddress { get; set; }

    [ConfigItem(Description = "The address of the Shutter key broadcast contract.",
        DefaultValue = "null")]
    string? KeyBroadcastContractAddress { get; set; }

    [ConfigItem(Description = "The address of the Shutter keyper set manager contract.",
        DefaultValue = "null")]
    string? KeyperSetManagerContractAddress { get; set; }

    [ConfigItem(Description = "The p2p addresses of the Shutter keypers.",
        DefaultValue = "[]")]
    string[] KeyperP2PAddresses { get; set; }

    [ConfigItem(Description = "The port to connect to Shutter P2P network with.",
        DefaultValue = "23001")]
    int P2PPort { get; set; }

    [ConfigItem(Description = "The filepath of the validator info json file.",
        DefaultValue = "null")]
    string? ValidatorInfoFile { get; set; }

    [ConfigItem(Description = "The Shutter P2P protocol version.",
        DefaultValue = "/shutter/0.1.0")]
    string P2PProtocolVersion { get; set; }

    [ConfigItem(Description = "The Shutter P2P agent version.",
        DefaultValue = "github.com/shutter-network/rolling-shutter/rolling-shutter")]
    string P2PAgentVersion { get; set; }

    [ConfigItem(Description = "The Shutter validator registry message version.",
        DefaultValue = "0")]
    ulong ValidatorRegistryMessageVersion { get; set; }

    [ConfigItem(Description = "Instance ID of Shutter keyper set.",
        DefaultValue = "0")]
    ulong InstanceID { get; set; }

    [ConfigItem(Description = "The maximum amount of gas to use on Shutter transactions.",
        DefaultValue = "10000000")]
    ulong EncryptedGasLimit { get; set; }
}

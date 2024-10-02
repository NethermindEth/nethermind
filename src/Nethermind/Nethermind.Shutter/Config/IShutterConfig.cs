// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Multiformats.Address;
using Nethermind.Config;
using Nethermind.Core;

namespace Nethermind.Shutter.Config;

public interface IShutterConfig : IConfig
{
    // todo: replace with bootnodes when peer discovery added
    private const string DefaultP2PAddresses =
@"/ip4/139.59.130.109/tcp/23003/p2p/12D3KooWRZoofMsnpsjkgvfPQUyGXZQnn7EVnb4tw4ghNfwMnnsj,
/ip4/167.71.169.248/tcp/23003/p2p/12D3KooWGH3VxoSQXZ6wUuCmsv5caGQnhwfGejbkXH6uS2r7sehA,
/ip4/139.59.130.109/tcp/23003/p2p/12D3KooWNxTiw7CvD1fuyye5P8qPhKTTrRBW6wwZwMdqdTxjYF2H,
/ip4/178.128.192.239/tcp/23003/p2p/12D3KooWCdpkipTiuzVMfkV7yLLgqbFeAL8WmEP78hCoBGBYLugN,
/ip4/45.55.192.248/tcp/23003/p2p/12D3KooWMPuubKqksfMxvLwEBDScaopTdvPLr5J5SMmBEo2zkcMz,
/ip4/178.128.126.237/tcp/23003/p2p/12D3KooWAg1pGUDAfFWSZftpN3JjBfLUCGLQcZApJHv2VntdMS9U";

    [ConfigItem(Description = "Whether to enable Shutter.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The filepath of the validator info json file.",
        DefaultValue = "null")]
    string? ValidatorInfoFile { get; set; }

    [ConfigItem(Description = "The address of the Shutter sequencer contract.",
        DefaultValue = "0xc5C4b277277A1A8401E0F039dfC49151bA64DC2E")]
    string? SequencerContractAddress { get; set; }

    [ConfigItem(Description = "The address of the Shutter validator registry contract.",
        DefaultValue = "0xefCC23E71f6bA9B22C4D28F7588141d44496A6D6")]
    string? ValidatorRegistryContractAddress { get; set; }

    [ConfigItem(Description = "The address of the Shutter key broadcast contract.",
        DefaultValue = "0x626dB87f9a9aC47070016A50e802dd5974341301")]
    string? KeyBroadcastContractAddress { get; set; }

    [ConfigItem(Description = "The address of the Shutter keyper set manager contract.",
        DefaultValue = "0x7C2337f9bFce19d8970661DA50dE8DD7d3D34abb")]
    string? KeyperSetManagerContractAddress { get; set; }

    [ConfigItem(Description = "The p2p addresses of the Shutter Keyper network bootnodes.",
        DefaultValue = DefaultP2PAddresses)]
    string[]? BootnodeP2PAddresses { get; set; }

    [ConfigItem(Description = "Instance ID of Shutter keyper set.",
        DefaultValue = "1000")]
    ulong InstanceID { get; set; }

    [ConfigItem(Description = "The port to connect to Shutter P2P network with.",
        DefaultValue = "23102")]
    int P2PPort { get; set; }

    [ConfigItem(Description = "The Shutter P2P protocol version.",
        DefaultValue = "/shutter/0.1.0", HiddenFromDocs = true)]
    string? P2PProtocolVersion { get; set; }

    [ConfigItem(Description = "The Shutter P2P agent version.",
        DefaultValue = "github.com/shutter-network/rolling-shutter/rolling-shutter",
        HiddenFromDocs = true)]
    string? P2PAgentVersion { get; set; }

    [ConfigItem(Description = "The Shutter validator registry message version.",
        DefaultValue = "0", HiddenFromDocs = true)]
    ulong ValidatorRegistryMessageVersion { get; set; }

    [ConfigItem(Description = "The maximum amount of gas to use on Shutter transactions.",
        DefaultValue = "10000000", HiddenFromDocs = true)]
    int EncryptedGasLimit { get; set; }

    [ConfigItem(Description = "Maximum amount of milliseconds into the slot to wait for Shutter keys before building block.",
        DefaultValue = "1666", HiddenFromDocs = true)]
    ushort MaxKeyDelay { get; }

    [ConfigItem(Description = "Whether to build Shutter blocks or just give metrics on Shutter transactions.",
        DefaultValue = "true", HiddenFromDocs = true)]
    bool Validator { get; set; }

    public void Validate()
    {
        if (Validator && ValidatorInfoFile is null)
        {
            throw new ArgumentException($"Must set Shutter.ValidatorInfoFile to a valid json file.");
        }

        if (ValidatorInfoFile is not null && !File.Exists(ValidatorInfoFile))
        {
            throw new ArgumentException($"Shutter validator info file \"{ValidatorInfoFile}\" does not exist.");
        }

        if (SequencerContractAddress is null || !Address.TryParse(SequencerContractAddress, out _))
        {
            throw new ArgumentException("Must set Shutter sequencer contract address to valid address.");
        }

        if (ValidatorRegistryContractAddress is null || !Address.TryParse(ValidatorRegistryContractAddress, out _))
        {
            throw new ArgumentException("Must set Shutter validator registry contract address to valid address.");
        }

        if (KeyBroadcastContractAddress is null || !Address.TryParse(KeyBroadcastContractAddress, out _))
        {
            throw new ArgumentException("Must set Shutter key broadcast contract address to valid address.");
        }

        if (KeyperSetManagerContractAddress is null || !Address.TryParse(KeyperSetManagerContractAddress, out _))
        {
            throw new ArgumentException("Must set Shutter keyper set manager contract address to valid address.");
        }

        if (P2PAgentVersion is null)
        {
            throw new ArgumentNullException(nameof(P2PAgentVersion));
        }

        if (P2PProtocolVersion is null)
        {
            throw new ArgumentNullException(nameof(P2PProtocolVersion));
        }

        if (BootnodeP2PAddresses is null)
        {
            throw new ArgumentNullException(nameof(BootnodeP2PAddresses));
        }

        foreach (string addr in BootnodeP2PAddresses)
        {
            try
            {
                Multiaddress.Decode(addr);
            }
            catch (NotSupportedException)
            {
                throw new ArgumentException($"Could not decode Shutter keyper p2p address \"{addr}\".");
            }
        }
    }
}

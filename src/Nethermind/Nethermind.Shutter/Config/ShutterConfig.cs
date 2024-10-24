// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Shutter.Config
{
    public class ShutterConfig : IShutterConfig
    {
        public bool Enabled { get; set; }
        public bool Validator { get; set; } = true;
        public string? SequencerContractAddress { get; set; } = "0xc5C4b277277A1A8401E0F039dfC49151bA64DC2E";
        public string? ValidatorRegistryContractAddress { get; set; } = "0xefCC23E71f6bA9B22C4D28F7588141d44496A6D6";
        public string? KeyBroadcastContractAddress { get; set; } = "0x626dB87f9a9aC47070016A50e802dd5974341301";
        public string? KeyperSetManagerContractAddress { get; set; } = "0x7C2337f9bFce19d8970661DA50dE8DD7d3D34abb";
        public string[]? BootnodeP2PAddresses { get; set; } = [];
        public int P2PPort { get; set; } = 23102;
        public string? ValidatorInfoFile { get; set; }
        public string? P2PProtocolVersion { get; set; } = "/shutter/0.1.0";
        public string? P2PAgentVersion { get; set; } = "github.com/shutter-network/rolling-shutter/rolling-shutter";
        public ulong ValidatorRegistryMessageVersion { get; set; } = 0;
        public ulong InstanceID { get; set; } = 1000;
        public int EncryptedGasLimit { get; set; } = 10000000;
        public ushort MaxKeyDelay { get; set; } = 1666;
    }
}

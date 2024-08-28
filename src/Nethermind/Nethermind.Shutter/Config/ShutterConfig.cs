// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Shutter.Config
{
    public class ShutterConfig : IShutterConfig
    {
        public bool Enabled { get; set; }
        public bool Validator { get; set; }
        public string? SequencerContractAddress { get; set; }
        public string? ValidatorRegistryContractAddress { get; set; }
        public string? KeyBroadcastContractAddress { get; set; }
        public string? KeyperSetManagerContractAddress { get; set; }
        public string[]? KeyperP2PAddresses { get; set; } = [];
        public int P2PPort { get; set; }
        public string? ValidatorInfoFile { get; set; }
        public string? P2PProtocolVersion { get; set; } = "/shutter/0.1.0";
        public string? P2PAgentVersion { get; set; } = "github.com/shutter-network/rolling-shutter/rolling-shutter";
        public ulong ValidatorRegistryMessageVersion { get; set; } = 0;
        public ulong InstanceID { get; set; } = 0;
        public ulong EncryptedGasLimit { get; set; } = 10000000;
        public ushort MaxKeyDelay { get; set; } = 1666;
    }
}

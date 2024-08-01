// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.AuRa.Config
{
    public class ShutterConfig : IShutterConfig
    {
        public bool Enabled { get; set; }
        public bool Validator { get; set; }
        public string ValidatorRegistryContractAddress { get; set; }
        public string SequencerContractAddress { get; set; }
        public string KeyBroadcastContractAddress { get; set; }
        public string KeyperSetManagerContractAddress { get; set; }
        public string[] KeyperP2PAddresses { get; set; }
        public int P2PPort { get; set; }
        public string ValidatorInfoFile { get; set; }
        public string P2PProtocolVersion { get; set; }
        public string P2PAgentVersion { get; set; }
        public ulong ValidatorRegistryMessageVersion { get; set; }
        public ulong InstanceID { get; set; }
        public ulong EncryptedGasLimit { get; set; }
        public uint MaxKeyDelay { get; set; } = 1666;
    }
}

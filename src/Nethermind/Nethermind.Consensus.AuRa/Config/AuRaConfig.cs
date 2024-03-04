// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.AuRa.Config
{
    public class AuRaConfig : IAuraConfig
    {
        public bool ForceSealing { get; set; } = true;

        public bool AllowAuRaPrivateChains { get; set; }

        public bool Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract { get; set; }

        public string TxPriorityContractAddress { get; set; }

        public string TxPriorityConfigFilePath { get; set; }

        public bool UseShutter { get; set; }
        public string ShutterValidatorRegistryContractAddress { get; set; }
        public string ShutterSequencerContractAddress { get; set; }
        public string ShutterKeyBroadcastContractAddress { get; set; }
        public string ShutterKeyperSetManagerContractAddress { get; set; }
        public string[] ShutterKeyperP2PAddresses { get; set; }
        public int ShutterP2PPort { get; set; }
    }
}

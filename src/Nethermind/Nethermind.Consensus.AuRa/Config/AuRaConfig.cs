// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Config
{
    public class AuRaConfig : IAuraConfig
    {
        public bool ForceSealing { get; set; } = true;

        public bool AllowAuRaPrivateChains { get; set; }

        public bool Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract { get; set; }

        public string TxPriorityContractAddress { get; set; }

        public string TxPriorityConfigFilePath { get; set; }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public static class SealEngineType
    {
        public const string None = nameof(None);
        public const string AuRa = nameof(AuRa);
        public const string Clique = nameof(Clique);
        public const string NethDev = nameof(NethDev);
        public const string Ethash = nameof(Ethash);
        public const string BeaconChain = nameof(BeaconChain);
        public const string Optimism = nameof(Optimism);
    }
}

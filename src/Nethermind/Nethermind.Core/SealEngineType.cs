// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public static class SealEngineType
    {
        public static readonly string None = nameof(None);
        public static readonly string AuRa = nameof(AuRa);
        public static readonly string Clique = nameof(Clique);
        public static readonly string NethDev = nameof(NethDev);
        public static readonly string Ethash = nameof(Ethash);
        public static readonly string BeaconChain = nameof(BeaconChain);
    }
}

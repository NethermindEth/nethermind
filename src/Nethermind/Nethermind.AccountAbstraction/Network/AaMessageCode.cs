// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.AccountAbstraction.Network
{
    public static class AaMessageCode
    {
        public const int UserOperations = 0x00;

        // more UserOperations-connected messages are planned to be added in the future
        // probably as a higher version of AaProtocolHandler. Commented out for now

        // public const int NewPooledUserOperationsHashes  = 0xab;
        // public const int GetPooledUserOperations = 0xac;
        // public const int PooledUserOperations  = 0xad;
    }
}

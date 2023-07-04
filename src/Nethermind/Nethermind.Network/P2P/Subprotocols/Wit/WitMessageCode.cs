// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Wit
{
    public static class WitMessageCode
    {
        /// <summary>
        /// Not used, reserved.
        /// </summary>
        public const int Status = 0x00;
        public const int GetBlockWitnessHashes = 0x01;
        public const int BlockWitnessHashes = 0x02;

        public static string GetDescription(int code)
        {
            return code switch
            {
                GetBlockWitnessHashes => nameof(GetBlockWitnessHashes),
                BlockWitnessHashes => nameof(BlockWitnessHashes),
                _ => $"Unknown({code.ToString()})"
            };
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Api
{
    public class ForkInformation
    {
        public ForkInformation(ulong chainId, Fork fork)
        {
            ChainId = chainId;
            Fork = fork;
        }

        public ulong ChainId { get; }
        public Fork Fork { get; }
    }
}

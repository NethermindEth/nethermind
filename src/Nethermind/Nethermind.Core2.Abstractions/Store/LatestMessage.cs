// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Store
{
    // Data Class
    public class LatestMessage
    {
        public LatestMessage(Epoch epoch, Root root)
        {
            Epoch = epoch;
            Root = root;
        }

        public Epoch Epoch { get; }
        public Root Root { get; }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.P2p
{
    public class PeeringStatus
    {
        public PeeringStatus(ForkVersion headForkVersion, Root finalizedRoot, Epoch finalizedEpoch, Root headRoot,
            Slot headSlot)
        {
            HeadForkVersion = headForkVersion;
            FinalizedRoot = finalizedRoot;
            FinalizedEpoch = finalizedEpoch;
            HeadRoot = headRoot;
            HeadSlot = headSlot;
        }

        public Epoch FinalizedEpoch { get; }
        public Root FinalizedRoot { get; }
        public ForkVersion HeadForkVersion { get; }
        public Root HeadRoot { get; }
        public Slot HeadSlot { get; }

        public override string ToString()
        {
            return
                $"fe={FinalizedEpoch}_fr={FinalizedRoot.ToString().Substring(0, 10)}_hs={HeadSlot}_hr={HeadRoot.ToString().Substring(0, 10)}";
        }
    }
}

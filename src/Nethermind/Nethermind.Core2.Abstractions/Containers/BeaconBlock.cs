// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class BeaconBlock
    {
        public BeaconBlock(Slot slot, Root parentRoot, Root stateRoot, BeaconBlockBody body)
        {
            Slot = slot;
            ParentRoot = parentRoot;
            StateRoot = stateRoot;
            Body = body;
        }

        public BeaconBlockBody Body { get; }
        public Root ParentRoot { get; }
        public Slot Slot { get; private set; }
        public Root StateRoot { get; private set; }

        public void SetStateRoot(Root stateRoot) => StateRoot = stateRoot;

        public override string ToString()
        {
            return $"s={Slot}_p={ParentRoot.ToString().Substring(0, 10)}_st={StateRoot.ToString().Substring(0, 10)}";
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Mev.Data
{
    public class BundleTransaction : Transaction
    {
        public Keccak BundleHash { get; set; } = Keccak.Zero;
        public bool CanRevert { get; set; } = false;
        public UInt256 SimulatedBundleFee { get; set; } = UInt256.Zero;
        public UInt256 SimulatedBundleGasUsed { get; set; } = UInt256.Zero;
        public BundleTransaction Clone() => (BundleTransaction)MemberwiseClone();
    }
}

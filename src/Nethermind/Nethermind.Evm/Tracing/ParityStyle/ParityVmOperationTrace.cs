// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing.ParityStyle
{
    //        {
    //            "cost": 0.0,
    //            "ex": {
    //                "mem": null,
    //                "push": [],
    //                "store": null,
    //                "used": 16961.0
    //            },
    //            "pc": 526.0,
    //            "sub": null
    //        }
    public class ParityVmOperationTrace
    {
        public long Cost { get; set; }
        public ParityMemoryChangeTrace Memory { get; set; }
        public byte[][] Push { get; set; }
        public ParityStorageChangeTrace Store { get; set; }
        public long Used { get; set; }
        public int Pc { get; set; }
        public ParityVmTrace Sub { get; set; }
    }
}

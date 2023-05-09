// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    //    "store": {
    //"key": "0x0",
    //"val": "0x486974636861696e"
    //},
    public class ParityStorageChangeTrace
    {
        public UInt256 Key { get; set; }
        public UInt256 Value { get; set; }
    }
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing.ParityStyle
{
    //    "store": {
    //"key": "0x0",
    //"val": "0x486974636861696e"
    //},
    public class ParityStorageChangeTrace
    {
        public byte[] Key { get; set; }
        public byte[] Value { get; set; }
    }
}

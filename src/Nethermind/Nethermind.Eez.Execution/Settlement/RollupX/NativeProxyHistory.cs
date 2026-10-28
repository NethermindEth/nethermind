// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal static class NativeProxyHistory
{
    internal static readonly Address ImplementationAddress = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee007a");
    internal static readonly Address AdminAddress = new("0x58ad165846a897f4aba3ec8f07a4a7db434324bf");
    internal static readonly ValueHash256 RuntimeHash = new("0x3725e8abaa83c431d55a24c39d4327791423019e9c8a2702cc68e6201105baf7");
    internal static readonly ValueHash256 ImplementationHash = new("0xb68626890d206c39d46bbb14288d62514b016af441c2b021b3517d04f2036291");
    internal static readonly ValueHash256 AdminHash = new("0x259ebef7fcfb8a540058d029b6f8c2abdb6a8dc62b4fca6b73a79fbde52473b3");
    internal static readonly UInt256 ImplementationSlot = new(new ValueHash256("0x360894a13ba1a3210667c828492db98dca3e2076cc3735a920a3ca505d382bbc").Bytes, isBigEndian: true);
    internal static readonly UInt256 AdminSlot = new(new ValueHash256("0xb53127684a568b3173ae13b9f8a6016e243e63b6e8ee1178d6a717850b5d6103").Bytes, isBigEndian: true);
}

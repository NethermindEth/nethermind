// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;

namespace Nethermind.Consensus.Processing;

[Flags]
public enum DumpOptions
{
    [Description("None.")]
    None = 0,
    [Description("Dumps block receipts traces.")]
    Receipts = 1,
    [Description("Dumps Parity-like traces.")]
    Parity = 2,
    [Description("Dumps Geth-like traces.")]
    Geth = 4,
    [Description("Dumps RLP data to a `.rlp` file with the block hash in the file name.")]
    Rlp = 8,
    [Description("Dumps RLP data to the log output.")]
    RlpLog = 16,
    [Description($"Combines the `{nameof(Receipts)}` `{nameof(Rlp)}` options.")]
    Default = Receipts | Rlp,
    [Description($"Combines the `{nameof(Geth)}` `{nameof(Parity)}` `{nameof(Receipts)}` `{nameof(Rlp)}` options.")]
    All = Receipts | Parity | Geth | Rlp
}

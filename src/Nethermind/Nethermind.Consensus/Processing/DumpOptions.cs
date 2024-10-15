// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;

namespace Nethermind.Consensus.Processing;

[Flags]
public enum DumpOptions
{
    [Description]
    None = 0,
    [Description]
    Receipts = 1,
    [Description]
    Parity = 2,
    [Description]
    Geth = 4,
    [Description]
    Rlp = 8,
    [Description]
    RlpLog = 16,
    [Description]
    Default = Receipts | Rlp,
    [Description]
    All = Receipts | Parity | Geth | Rlp
}

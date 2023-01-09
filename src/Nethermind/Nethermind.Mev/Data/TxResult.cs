// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mev.Data
{
    public class TxResult
    {
        public byte[]? Value { get; set; }
        public byte[]? Error { get; set; }
    }
}

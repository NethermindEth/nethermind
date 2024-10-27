// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.T8NTool;

public class RejectedTx
{
    public RejectedTx(int index, string error)
    {
        Index = index;
        Error = error;
    }

    public int Index { get; set; }
    public string? Error { get; set; }
}

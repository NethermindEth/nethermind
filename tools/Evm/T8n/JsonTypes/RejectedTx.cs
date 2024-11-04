// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.T8n.JsonTypes;

public class RejectedTx(int index, string error)
{
    public int Index { get; set; } = index;
    public string? Error { get; set; } = error;
}

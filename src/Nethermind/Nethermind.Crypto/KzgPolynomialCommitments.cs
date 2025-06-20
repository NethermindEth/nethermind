// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;

namespace Nethermind.Crypto;

public static class KzgPolynomialCommitments
{
    public static void ComputeCellProofs(ReadOnlySpan<byte> blob, Span<byte> cellProofs)
    {
        Ckzg.ComputeCellsAndKzgProofs(new byte[Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell], cellProofs, blob, 0);
    }
}


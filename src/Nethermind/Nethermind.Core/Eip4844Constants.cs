// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core;

public class Eip4844Constants
{
    public const int MinBlobsPerTransaction = 1;
    public const int MaxBlobsPerBlock = 6;

    public const ulong BlobGasPerBlob = 1 << 17;
    public const ulong MaxBlobGasPerBlock = BlobGasPerBlob * MaxBlobsPerBlock;
    public const ulong TargetBlobGasPerBlock = MaxBlobGasPerBlock / 2;
    public const ulong MaxBlobGasPerTransaction = MaxBlobGasPerBlock;

    public static readonly UInt256 BlobGasUpdateFraction = 3338477;
    public static readonly UInt256 MinBlobGasPrice = 1;
}

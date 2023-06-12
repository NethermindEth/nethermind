// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core;

public class Eip4844Constants
{
    public const int MinBlobsPerTransaction = 1;

    public const ulong DataGasPerBlob = 1 << 17;
    public const ulong TargetDataGasPerBlock = DataGasPerBlob * 3;
    public const ulong MaxDataGasPerBlock = DataGasPerBlob * 6;
    public const ulong MaxDataGasPerTransaction = MaxDataGasPerBlock;

    public static readonly UInt256 DataGasUpdateFraction = 3338477;
    public static readonly UInt256 MinDataGasPrice = 1;
}

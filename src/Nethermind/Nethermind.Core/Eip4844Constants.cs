// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

public class Eip4844Constants
{
    public const int MinBlobsPerTransaction = 1;

    public const int DataGasPerBlob = 1 << 17;
    public const int TargetDataGasPerBlock = 1 << 18;
    public const int MaxDataGasPerBlock = 1 << 19;
    public const int MaxDataGasPerTransaction = MaxDataGasPerBlock;
}

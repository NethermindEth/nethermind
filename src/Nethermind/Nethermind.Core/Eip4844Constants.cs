// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

public class Eip4844Constants
{
    public const int MinBlobsPerTransaction = 1;

    public const ulong DataGasPerBlob = 1 << 17;
    public const ulong TargetDataGasPerBlock = 1 << 18;
    public const ulong MaxDataGasPerBlock = 1 << 19;
    public const ulong MaxDataGasPerTransaction = MaxDataGasPerBlock;
}

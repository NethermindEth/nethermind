// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

public class Eip4844Constants
{
    public const int MaxBlobsPerBlock = 4;
    public const int MaxBlobsPerTransaction = MaxBlobsPerBlock;
}

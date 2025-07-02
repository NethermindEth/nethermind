// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core.Extensions;

namespace Nethermind.HealthChecks;

public static class DriveInfoExtensions
{
    public static double GetFreeSpacePercentage(this IDriveInfo driveInfo) =>
        driveInfo.AvailableFreeSpace * 100.0 / driveInfo.TotalSize;

    public static double GetFreeSpaceInGiB(this IDriveInfo driveInfo) =>
        (double)driveInfo.AvailableFreeSpace / 1.GiB();
}

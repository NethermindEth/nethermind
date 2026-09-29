// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core.Extensions;
using NSubstitute;

namespace Nethermind.HealthChecks.Test;

internal static class DiskSpaceTestHelper
{
    public static readonly long FreeSpaceBytes = (long)(1.GiB * 1.2);

    public static IDriveInfo[] GetDriveInfos(float availableDiskSpacePercent)
    {
        IDriveInfo drive = Substitute.For<IDriveInfo>();
        drive.AvailableFreeSpace.Returns(FreeSpaceBytes);
        drive.TotalSize.Returns((long)(FreeSpaceBytes * 100.0 / availableDiskSpacePercent));
        drive.RootDirectory.FullName.Returns("C:/");

        return new[] { drive };
    }
}

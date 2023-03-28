// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int GeneralError = 1;

    public const int EnvironmentVariableConfigChanged = 99;

    // config errors 100...199
    public const int NoEngineModule = 100;

    public const int NoDownloadOldReceiptsOrBlocks = 101;
    public const int TooLongExtraData = 102;
    public const int ConflictingConfigurations = 103;
    public const int LowDiskSpace = 104;
}

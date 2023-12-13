// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int GeneralError = 1;

    // config errors 100...199
    public const int NoEngineModule = 100;

    public const int NoDownloadOldReceiptsOrBlocks = 101;
    public const int TooLongExtraData = 102;
    public const int ConflictingConfigurations = 103;
    public const int LowDiskSpace = 104;

    // Posix exit code
    // https://tldp.org/LDP/abs/html/exitcodes.html
    public const int SigInt = 130; // 128 + 2 (sigint)
    public const int SigTerm = 143; // 128 + 15 (sigterm)
}

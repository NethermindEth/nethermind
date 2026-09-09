// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Precompiles;

public static class Errors
{
    public const string Failed = "failed";
    public const string InvalidFieldElementTopBytes = "invalid field element top bytes";
    public const string InvalidFieldLength = "invalid field element length";
    public const string InvalidFinalBlockFlag = "incorrect final block indicator flag";
    public const string InvalidInputLength = "invalid input length";
    public const string G1NotOnCurve = "g1 point is not on curve";
    public const string G1PointSubgroup = "g1 point is not on correct subgroup";
    public const string G2NotOnCurve = "g2 point is not on curve";
    public const string G2PointSubgroup = "g2 point is not on correct subgroup";
    public const string L1StorageAccessFailed = "l1 storage access failed";
}

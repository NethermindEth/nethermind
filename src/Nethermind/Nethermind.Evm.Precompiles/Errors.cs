// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

public static class Errors
{
    public const string? NoError = null;

    public const string InvalidInputLength = "invalid input length";
    public const string InvalidFieldLength = "invalid field element length";
    public const string InvalidFieldElementTopBytes = "invalid field element top bytes";
    public const string G1PointSubgroup = "g1 point is not on correct subgroup";
    public const string G2PointSubgroup = "g2 point is not on correct subgroup";

    public const string InvalidFinalFlag = "invalid final flag";
    public const string L1StorageAccessFailed = "l1 storage access failed";
    public const string  Overflow = "overflow";

    public const string Failed = "failed";


}

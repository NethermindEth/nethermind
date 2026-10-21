// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Precompiles;

/// <summary>Reasons a precompile rejects its input.</summary>
/// <remarks>The texts are go-ethereum's, which JSON-RPC callers see as the error of a call that fails on them.</remarks>
public static class Errors
{
    public const string Failed = "failed";
    public const string InvalidFieldElementTopBytes = "invalid field element top bytes";
    public const string InvalidFieldLength = "invalid field element length";
    public const string InvalidFieldElementEncoding = "invalid fp.Element encoding";
    public const string InvalidFinalBlockFlag = "invalid final flag";
    public const string InvalidInputLength = "invalid input length";
    public const string NotOnCurve = "invalid point: not on curve";
    public const string G1PointSubgroup = "g1 point is not on correct subgroup";
    public const string G2PointSubgroup = "g2 point is not on correct subgroup";
    public const string Bn254NotOnCurve = "point is not on curve";
    public const string Bn254NotInSubgroup = "point is not in correct subgroup";
    public const string Bn254PairingInputLength = "bad elliptic curve pairing size";
    public const string MismatchedVersionedHash = "mismatched versioned hash";
    public const string L1StorageAccessFailed = "l1 storage access failed";
}

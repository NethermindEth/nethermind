// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public record struct ValidationResult(string? Error, bool AllowTxPoolReentrance = false)
{
    public static ValidationResult Success => new(null);
    public static implicit operator bool(ValidationResult result) => result.AsBool();
    public static implicit operator ValidationResult(string error) => new(error);
    public override readonly string ToString() => Error ?? "Success";
    public readonly bool AsBool() => Error is null;
}

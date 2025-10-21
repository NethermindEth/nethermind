// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public readonly record struct ValidationResult(string? Error)
{
    public static ValidationResult Success => new(null);
    public static implicit operator bool(ValidationResult result) => result.AsBool();
    public static implicit operator ValidationResult(string error) => new(error);
    public override string ToString() => Error ?? "Success";
    public bool AsBool() => Error is null;
}

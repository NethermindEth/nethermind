// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

public readonly record struct ValidationResult(string? Error)
{
    // A classified failure uses the same storage as an ordinary error string, keeping hot-path results one reference wide.
    private readonly object? _error = Error;

    /// <summary>The validation diagnostic, or null for success.</summary>
    public string? Error
    {
        get => _error is TxFailure failure ? failure.Message : (string?)_error;
        init => _error = value;
    }

    /// <summary>The specific reason supplied by a transaction validator, when available.</summary>
    public TxValidationError TxError => _error is TxFailure failure ? failure.Reason : TxValidationError.None;

    /// <summary>Creates a failed result carrying a diagnostic message and a transaction rejection reason.</summary>
    public ValidationResult(string error, TxValidationError reason) : this(null)
    {
        ArgumentNullException.ThrowIfNull(error);
        _error = new TxFailure(error, reason);
    }

    public static ValidationResult Success => new(null);
    public static implicit operator bool(ValidationResult result) => result.AsBool();
    public static implicit operator ValidationResult(string error) => new(error);
    public override string ToString() => Error ?? "Success";
    public bool AsBool() => _error is null;
    public bool Equals(ValidationResult other) => string.Equals(Error, other.Error, StringComparison.Ordinal);
    public override int GetHashCode() => Error?.GetHashCode() ?? 0;

    private sealed class TxFailure(string message, TxValidationError reason)
    {
        public string Message { get; } = message;
        public TxValidationError Reason { get; } = reason;
    }
}

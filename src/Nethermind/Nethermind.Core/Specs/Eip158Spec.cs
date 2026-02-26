// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Specs;

/// <summary>
/// A small struct carrying only the EIP-158 information needed by <c>IWorldState</c>
/// balance and code-insertion methods. Stored as a field on <see cref="SpecSnapshot"/>
/// — never allocated per-call.
/// </summary>
public readonly struct Eip158Spec(bool isEnabled, Address? ignoredAccount) : IEquatable<Eip158Spec>
{
    public readonly bool IsEnabled = isEnabled;
    public readonly Address? IgnoredAccount = ignoredAccount;

    public bool Equals(Eip158Spec other) => IsEnabled == other.IsEnabled && IgnoredAccount == other.IgnoredAccount;
    public override bool Equals(object? obj) => obj is Eip158Spec other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(IsEnabled, IgnoredAccount);
}

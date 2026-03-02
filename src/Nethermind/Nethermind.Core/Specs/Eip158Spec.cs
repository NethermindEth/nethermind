// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// A small struct carrying only the EIP-158 information needed by <c>IWorldState</c>
/// balance and code-insertion methods. Stored as a field on <see cref="SpecSnapshot"/>
/// — never allocated per-call.
/// </summary>
public readonly record struct Eip158Spec(bool IsEnabled, Address? IgnoredAccount);

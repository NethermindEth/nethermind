// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// A small struct carrying the spec information needed by <c>ICodeInfoRepository</c>
/// code-insertion and delegation methods. Owns <see cref="Eip158Spec"/> — on cached
/// representations (<see cref="SpecSnapshot"/>, <c>BlockExecutionContext</c>) there is
/// only one <see cref="Eip158Spec"/> instance, living inside this struct.
/// </summary>
public readonly record struct CodeInsertionSpec(Eip158Spec Eip158, bool IsEofEnabled);

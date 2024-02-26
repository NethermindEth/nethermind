// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>
/// Represent a list that that should be disposed.
/// Conventionally:
/// - If this is returned from a method, the method caller should dispose it.
/// - If this is passed to a method, the object for the method should dispose it.
/// Maybe should be called `IOwnedReadOnlyList<t>` instead.
///
/// TODO: One day, check if https://github.com/dotnet/roslyn-analyzers/issues/1617 has progressed.
///
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IDisposableReadOnlyList<T> : IReadOnlyList<T>, IDisposable
{
}

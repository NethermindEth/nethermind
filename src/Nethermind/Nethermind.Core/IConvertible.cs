// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// A hopefully standard interface to define a type that can be converted to T.
/// Is there really no standard way to define this?
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IConvertible<T>
{
    T Convert();
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Container;

/// <summary>
/// Mark a constructor to be used by nethermind DI. Apply when any of the constructor have this attribute.
/// </summary>
public class UseConstructorForDependencyInjectionAttribute: Attribute
{
}

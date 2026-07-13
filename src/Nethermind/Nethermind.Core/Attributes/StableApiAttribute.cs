// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Attributes;

/// <summary>
/// Marks an interface that is kept more stable than the rest of the codebase, so that plugins can rely on it.
/// </summary>
/// <remarks>
/// Any change to a <c>[StableApi]</c> interface must be described in the pull request. Core code must not cast
/// or type-check a value against it (a plugin may decorate or override it); the <c>NETH008</c> analyzer enforces this.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class StableApiAttribute : Attribute;

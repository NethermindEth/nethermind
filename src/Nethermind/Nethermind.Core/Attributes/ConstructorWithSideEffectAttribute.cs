// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Attributes;

/// <summary>
/// Marks a constructor as intentionally having side effects (e.g., event subscriptions,
/// service registration). This suppresses the NETH001 analyzer warning when the constructed
/// object is not stored or used, since the construction itself is the intended action.
/// Use this attribute when the type exists primarily for its side effects. For one-off
/// cases where a normally-used type is constructed just for its side effects, prefer
/// the call-site discard pattern: <c>_ = new T(...);</c>
/// </summary>
[AttributeUsage(AttributeTargets.Constructor)]
public sealed class ConstructorWithSideEffectAttribute : Attribute;

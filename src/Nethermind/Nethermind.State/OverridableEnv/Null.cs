// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Placeholder component for an <see cref="IOverridableEnv{T}"/> whose caller only needs the override
/// scope's lifetime, not a resolved component bundle — <c>Scope&lt;Null&gt;.Component</c> is unused.
/// </summary>
public sealed class Null { }

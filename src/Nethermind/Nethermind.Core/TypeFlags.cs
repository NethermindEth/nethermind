// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents a flag interface that declares a static boolean property.
/// By defining the property as static and using generic specialization at the JIT level,
/// any conditional checks (e.g. if-branches) can be elided (i.e., removed), as the compiler 
/// can resolve the static value at compile-time rather than needing to evaluate it at runtime.
/// </summary>
public interface IFlag
{
    /// <summary>
    /// Gets a value indicating whether this flag is active.
    /// The JIT can specialize the implementation based on the static type, 
    /// removing the need for run-time checks.
    /// </summary>
    virtual static bool IsActive { get; }
}

/// <summary>
/// A concrete implementation of <see cref="IFlag"/> where <c>IsActive</c> is statically false.
/// The JIT can use this known constant to eliminate any conditional branching at run time.
/// </summary>
public struct OffFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}

/// <summary>
/// A concrete implementation of <see cref="IFlag"/> where <c>IsActive</c> is statically true.
/// The JIT can use this known constant to eliminate any conditional branching at run time.
/// </summary>
public struct OnFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => true;
}

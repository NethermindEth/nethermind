// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Attributes;

/// <summary>
/// Marks an interface member whose default implementation is a no-op fallback for
/// non-decorating implementers. Any class that implements the same interface AND has a
/// field of that interface type (i.e. wraps another implementation) MUST explicitly
/// implement the tagged member and forward the call to its inner instance.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class MustForwardOnDecorateAttribute : Attribute;

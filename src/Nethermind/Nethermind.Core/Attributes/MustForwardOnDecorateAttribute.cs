// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Attributes;

/// <summary>
/// Marks an interface member whose default implementation is a no-op fallback for
/// non-decorating implementers. Any class that implements the same interface AND has a
/// field of that interface type (i.e. wraps another implementation) MUST explicitly
/// implement the tagged member and forward the call to its inner instance. The NETH006
/// analyzer enforces this; the attribute itself has no runtime effect.
/// </summary>
/// <remarks>
/// Decorators that forget to forward such members silently swallow side effects on
/// nested implementations — e.g. a witness-recording world state buried under a
/// decorator chain would never observe access events the EVM raises through the outer
/// type. Tagging the member surfaces those omissions at build time instead of as
/// runtime witness mismatches.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class MustForwardOnDecorateAttribute : Attribute;

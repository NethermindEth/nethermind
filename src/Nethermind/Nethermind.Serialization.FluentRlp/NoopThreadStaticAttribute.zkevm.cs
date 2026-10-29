// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Takes the place of <see cref="ThreadStaticAttribute"/> in the zkEVM guest, which runs on one thread.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
internal sealed class NoopThreadStaticAttribute : Attribute;

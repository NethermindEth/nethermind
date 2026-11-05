// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Attributes;

[AttributeUsage(AttributeTargets.Field)]
public sealed class NoopThreadStaticAttribute : Attribute { }

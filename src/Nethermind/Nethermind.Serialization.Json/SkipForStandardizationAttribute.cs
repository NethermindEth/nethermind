// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json;

[AttributeUsage(AttributeTargets.Property)]
public class SkipForStandardizationAttribute : Attribute
{
}

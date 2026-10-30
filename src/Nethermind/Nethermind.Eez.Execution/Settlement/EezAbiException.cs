// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>ABI data that does not decode, or that is not the canonical encoding of what it decodes to.</summary>
public sealed class EezAbiException(string message) : Exception(message);

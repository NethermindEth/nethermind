// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Mcp.Dto;

public sealed record NodeConfigDto(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Sections);

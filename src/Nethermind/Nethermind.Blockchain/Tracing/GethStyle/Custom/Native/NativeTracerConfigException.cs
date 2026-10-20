// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;

internal sealed class NativeTracerConfigException(JsonException inner) : JsonException(inner.Message, inner);

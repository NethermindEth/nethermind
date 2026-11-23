// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;

internal sealed class NativeTracerConfigException(string message, Exception? inner = null) : JsonException(message, inner);

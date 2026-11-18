// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public sealed class TraceDataLimitExceededException(long limit)
    : Exception($"Trace data exceeds the limit of {limit} bytes.");

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>
/// The request resolved to a beacon state of a fork this driver cannot decode. Raised only by
/// <see cref="ApiStateDecoding"/>, the one place the API layer knows that is what the codec's refusal
/// means, so the error middleware can map exactly this condition to 501 rather than every
/// <see cref="NotSupportedException"/> from every layer. The message is for the log, never the wire.
/// </summary>
internal sealed class UnsupportedForkException(Exception codecRefusal)
    : Exception("The requested beacon state belongs to a fork this driver cannot decode", codecRefusal);

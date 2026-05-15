// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Era1;

namespace Nethermind.EraE.Exceptions;

internal class EraFormatException(string message) : EraException(message);

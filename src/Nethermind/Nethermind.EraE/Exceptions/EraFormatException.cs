// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Exceptions;

[Serializable]
internal class EraFormatException(string message) : EraException(message);

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE;
[Serializable]
internal class EraFormatException(string message) : Era1.EraException(message);

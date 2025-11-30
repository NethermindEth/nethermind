// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public class EraException(string message) : Exception(message);

[Serializable]
internal class EraFormatException(string message) : EraException(message);

public class EraVerificationException(string message) : EraException(message);

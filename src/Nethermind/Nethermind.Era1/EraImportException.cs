// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Era1.Exceptions;

namespace Nethermind.Era1;

public class EraImportException(string message) : EraException(message);

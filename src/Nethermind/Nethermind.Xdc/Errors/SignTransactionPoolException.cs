// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.Errors;

internal class SignTransactionPoolException(string message)
    : Exception(message)
{
}

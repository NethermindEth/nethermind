// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

namespace Nethermind.Sockets;

public sealed class SocketSendLimitException(string message, Exception? innerException = null) : IOException(message, innerException);

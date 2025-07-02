// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Evm.EvmObjectFormat;
interface IEofVersionHandler
{
    bool TryParseEofHeader(ReadOnlyMemory<byte> code, ValidationStrategy strategy, [NotNullWhen(true)] out EofHeader? header);
    bool TryGetEofContainer(ValidationStrategy strategy, [NotNullWhen(true)] out EofContainer? header, ReadOnlyMemory<byte> code);
}

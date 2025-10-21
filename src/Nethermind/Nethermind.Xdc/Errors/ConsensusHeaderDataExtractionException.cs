// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Errors;

public class ConsensusHeaderDataExtractionException(string fieldType, Exception? innerException = null)
    : Exception($"Failed to get {fieldType} from header", innerException)
{
    public string FieldType { get; } = fieldType;
}

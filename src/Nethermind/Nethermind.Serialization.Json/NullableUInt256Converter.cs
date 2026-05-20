// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

public class NullableUInt256Converter() : NullableJsonConverter<UInt256>(new UInt256Converter());

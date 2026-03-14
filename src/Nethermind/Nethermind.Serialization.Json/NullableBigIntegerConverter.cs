// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Serialization.Json;

public class NullableBigIntegerConverter() : NullableJsonConverter<BigInteger>(new BigIntegerConverter());

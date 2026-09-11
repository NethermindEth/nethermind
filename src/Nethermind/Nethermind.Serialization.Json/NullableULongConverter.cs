// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Json;

public class NullableULongConverter : NullableJsonConverter<ulong>
{
    public NullableULongConverter() : base(new ULongConverter()) { }
    public NullableULongConverter(bool strictQuantity) : base(new ULongConverter(strictQuantity)) { }
}

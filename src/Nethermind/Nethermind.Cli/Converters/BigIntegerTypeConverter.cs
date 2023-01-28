// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;

namespace Nethermind.Cli.Converters
{
    public class BigIntegerTypeConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
            sourceType == typeof(double)
            || sourceType == typeof(decimal)
            || sourceType == typeof(float)
            || base.CanConvertFrom(context, sourceType);

        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) => destinationType == typeof(BigInteger);

        public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            return value switch
            {
                float f => (BigInteger)f,
                double d => (BigInteger)d,
                decimal d => (BigInteger)d,
                _ => base.ConvertFrom(context, culture, value)
            } ?? throw new InvalidOperationException();
        }
    }
}

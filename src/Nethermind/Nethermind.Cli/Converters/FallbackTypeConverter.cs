// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Linq;
using Jint.Runtime.Interop;

namespace Nethermind.Cli.Converters
{
    public class FallbackTypeConverter : ITypeConverter
    {
        private readonly ITypeConverter _defaultConverter;
        private readonly TypeConverter[] _converters;

        public FallbackTypeConverter(ITypeConverter defaultConverter, params TypeConverter[] converters)
        {
            _defaultConverter = defaultConverter;
            _converters = converters;
        }

        public object Convert(object value, Type type, IFormatProvider formatProvider)
        {
            TypeConverter? converter = GetConverter(type, GetFromType(value));
            return converter?.ConvertFrom(value) ?? _defaultConverter.Convert(value, type, formatProvider);
        }

        public bool TryConvert(object value, Type type, IFormatProvider formatProvider, out object? converted)
        {
            TypeConverter? converter = GetConverter(type, GetFromType(value));

            bool result;
            if (converter is not null)
            {
                converted = converter.ConvertFrom(value);
                result = true;
            }
            else
            {
                result = _defaultConverter.TryConvert(value, type, formatProvider, out converted);
            }

            return result;
        }

        private static Type GetFromType(object? value) => value?.GetType() ?? typeof(object);

        private TypeConverter? GetConverter(Type toType, Type fromType) =>
            _converters.FirstOrDefault(c => c.CanConvertTo(toType) && c.CanConvertFrom(fromType));
    }
}

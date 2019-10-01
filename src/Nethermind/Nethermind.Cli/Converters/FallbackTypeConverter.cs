//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.ComponentModel;
using System.Globalization;
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
            var converter = GetConverter(type, GetFromType(value));
            return converter != null ? converter.ConvertFrom(value) : _defaultConverter.Convert(value, type, formatProvider);
        }

        public bool TryConvert(object value, Type type, IFormatProvider formatProvider, out object converted)
        {
            var converter = GetConverter(type, GetFromType(value));
            if (converter != null)
            {
                converted = converter.ConvertFrom(value);
                return true;
            }
            else
            {
                return _defaultConverter.TryConvert(value, type, formatProvider, out converted);
            }
        }
        
        private static Type GetFromType(object value) => value?.GetType() ?? typeof(object);
        
        private TypeConverter GetConverter(Type toType, Type fromType) => _converters.FirstOrDefault(c => c.CanConvertTo(toType) && c.CanConvertFrom(fromType));
    }
}
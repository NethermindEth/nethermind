//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.IO;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    public class ConverterTestBase<T>
    {
        protected void TestConverter(T item, Func<T, T, bool> equalityComparer, JsonConverter<T> converter)
        {
            JsonSerializer serializer = new JsonSerializer();
            serializer.Converters.Add(converter);
            StringBuilder builder = new StringBuilder();
            StringWriter writer = new StringWriter(builder);
            serializer.Serialize(writer, item);
            string result = builder.ToString();
            JsonReader reader = new JsonTextReader(new StringReader(result));
            T deserialized = serializer.Deserialize<T>(reader);

            Assert.True(equalityComparer(item, deserialized));
        }
    }
}

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
// 

using System;
using System.IO;
using System.Text;

namespace Nethermind.Serialization.Json
{
    public class CountingTextWriter : TextWriter
    {
        private readonly TextWriter _textWriter;

        public long Size { get; set; }

        public CountingTextWriter(TextWriter textWriter)
        {
            _textWriter = textWriter ?? throw new ArgumentNullException(nameof(textWriter));
        }

        public override Encoding Encoding => _textWriter.Encoding;

        public override void Write(char value)
        {
            _textWriter.Write(value);
            Size++;
        }

        public override void Flush()
        {
            _textWriter.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _textWriter.Dispose();
            }
        }
    }
}

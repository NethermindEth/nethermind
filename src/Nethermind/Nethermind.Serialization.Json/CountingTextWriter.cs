// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;

namespace Nethermind.Serialization.Json
{
    public class CountingTextWriter : TextWriter
    {
        private readonly TextWriter _textWriter;

        public long Size { get; private set; }

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

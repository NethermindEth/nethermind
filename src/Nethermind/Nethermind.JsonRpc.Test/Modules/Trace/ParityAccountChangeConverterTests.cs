using System.Collections.Generic;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Trace;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [TestFixture]
    public class ParityAccountChangeConverterTests
    {
        private ParityAccountStateChangeConverter converter;

        [SetUp]
        public void SetUp()
        {
            converter = new ParityAccountStateChangeConverter();
        }

        [Test]
        public void Does_not_throw_on_change_when_code_after_is_null()
        {
            JsonWriter writer = Substitute.For<JsonWriter>();
            JsonSerializer serializer = Substitute.For<JsonSerializer>();

            ParityAccountStateChange change = new ParityAccountStateChange
            {
                Code = new ParityStateChange<byte[]>(new byte[] {1}, null)
            };

            Assert.DoesNotThrow(() => converter.WriteJson(writer, change, serializer));
        }

        [Test]
        public void Does_not_throw_on_change_when_code_before_is_null()
        {
            JsonWriter writer = Substitute.For<JsonWriter>();
            JsonSerializer serializer = Substitute.For<JsonSerializer>();

            ParityAccountStateChange change = new ParityAccountStateChange
            {
                Code = new ParityStateChange<byte[]>(null, new byte[] {1})
            };

            Assert.DoesNotThrow(() => converter.WriteJson(writer, change, serializer));
        }

        [Test]
        public void Does_not_throw_on_change_storage()
        {
            JsonWriter writer = Substitute.For<JsonWriter>();
            JsonSerializer serializer = Substitute.For<JsonSerializer>();

            ParityAccountStateChange change = new ParityAccountStateChange
            {
                Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>
                {
                    {1, new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {0})}
                }
            };

            Assert.DoesNotThrow(() => converter.WriteJson(writer, change, serializer));
        }
    }
}
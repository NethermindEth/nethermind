// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Serialization.FluentRlp.Instances;

namespace Nethermind.Serialization.FluentRlp.Test;

public class RlpReadWriteTest
{
    [Test]
    public void LongString()
    {
        byte[] rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            string str = new('A', 2000);
            w.Write(str);
        });

        string decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.ReadString());

        Assert.That(decoded, Is.EqualTo(new string('A', 2000)));
    }

    [Test]
    public void HeterogeneousList()
    {
        byte[] rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.WriteSequence(static (ref RlpWriter w) => { w.Write(42); });
                w.WriteSequence(static (ref RlpWriter w) =>
                {
                    w.Write("dog");
                    w.Write("cat");
                });
            });
        });

        (int, (string, string)) decoded = Rlp.Read(rlp, (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                int _1 = r.ReadSequence(static (scoped ref RlpReader r) => r.ReadInt32());
                (string, string) _2 = r.ReadSequence(static (scoped ref RlpReader r) =>
                {
                    string _1 = r.ReadString();
                    string _2 = r.ReadString();

                    return (_1, _2);
                });

                return (_1, _2);
            });
        });

        Assert.That(decoded, Is.EqualTo((42, ("dog", "cat"))));
    }

    [Test]
    public void LongList()
    {
        byte[] rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                for (int i = 0; i < 100; i++)
                {
                    w.Write("dog");
                }
            });
        });

        List<string> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<string> result = [];
                for (int i = 0; i < 100; i++)
                {
                    result.Add(r.ReadString());
                }

                return result;
            });
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Has.Count.EqualTo(100));
            Assert.That(decoded, Is.All.EqualTo("dog"));
        }
    }

    [Test]
    public void MultipleLongList()
    {
        byte[] rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                for (int i = 0; i < 100; i++)
                {
                    w.Write("dog");
                }
            });
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                for (int i = 0; i < 50; i++)
                {
                    w.Write("cat");
                }
            });
        });

        (List<string> dogs, List<string> cats) = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            List<string> dogs = r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<string> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadString());
                }

                return result;
            });
            List<string> cats = r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<string> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadString());
                }

                return result;
            });

            return (dogs, cats);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dogs, Has.Count.EqualTo(100));
            Assert.That(dogs, Is.All.EqualTo("dog"));
            Assert.That(cats, Has.Count.EqualTo(50));
            Assert.That(cats, Is.All.EqualTo("cat"));
        }
    }

    [Test]
    public void UnknownLengthList([Values(1, 2, 3, 5, 10, 20)] int length)
    {
        byte[] rlp = Rlp.Write(length, static (ref RlpWriter root, int length) =>
        {
            root.WriteSequence(length, static (ref RlpWriter w, int length) =>
            {
                for (int i = 0; i < length; i++)
                {
                    w.Write(42);
                }
            });
        });

        List<int> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<int> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadInt32());
                }

                return result;
            });
        });

        Assert.That(decoded, Has.Count.EqualTo(length));
    }

    [Test]
    public void InvalidObjectReading()
    {
        byte[] rlp = Rlp.Write(static (ref RlpWriter w) => { w.Write(42); });
        Action tryRead = () => Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader _) => null as object);
        });

        Assert.That(tryRead, Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void InvalidListReading()
    {
        byte[] rlp = Rlp.Write(static (ref RlpWriter w) => { w.WriteSequence(static (ref RlpWriter _) => { }); });
        Func<int> tryRead = () => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadInt32());

        Assert.That(tryRead, Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void Choice()
    {
        RefRlpReaderFunc<int> intReader = static (scoped ref RlpReader r) => r.ReadInt32();
        RefRlpReaderFunc<int> wrappedReader = (scoped ref RlpReader r) => r.ReadSequence(intReader);
        byte[] intRlp = Rlp.Write(static (ref RlpWriter w) => { w.Write(42); });
        byte[] wrappedIntRlp = Rlp.Write(static (ref RlpWriter w) => w.WriteSequence(static (ref RlpWriter w) => { w.Write(42); }));

        foreach (byte[] rlp in (byte[][])[intRlp, wrappedIntRlp])
        {
            int decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.Choice(wrappedReader, intReader));

            Assert.That(decoded, Is.EqualTo(42));
        }
    }

    [Test]
    public void ChoiceDeep()
    {
        RefRlpReaderFunc<(string, string, string)> readerA = static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                string _1 = r.ReadString();
                string _2 = r.ReadString();
                string _3 = r.ReadString();

                return (_1, _2, _3);
            });
        };
        RefRlpReaderFunc<(string, string, string)> readerB = static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                string _1 = r.ReadString();
                string _2 = r.ReadString();
                int _3 = r.ReadInt32();

                return (_1, _2, _3.ToString());
            });
        };

        byte[] rlp = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.Write("dog");
                w.Write("cat");
                // 0x03e8 is not valid UTF-8, so readerA fails on it.
                w.Write(1000);
            });
        });

        (string, string, string) decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.Choice(readerA, readerB));
        Assert.That(decoded, Is.EqualTo(("dog", "cat", "1000")));
    }

    [Test]
    public void OptionalStruct()
    {
        int? value = null;

        byte[] rlp = Rlp.Write(value, static (ref RlpWriter w, int? value) =>
        {
            if (value.HasValue)
            {
                w.Write(value.Value);
            }
        });

        int? decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.Optional(static (scoped ref RlpReader r) => r.ReadInt32());
        });

        Assert.That(decoded, Is.EqualTo(value));
    }


    [Test]
    public void OptionalReference()
    {
        string? value = null;

        byte[] rlp = Rlp.Write(value, static (ref RlpWriter w, string? value) =>
        {
            if (value is not null)
            {
                w.Write(value);
            }
        });

        string? decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.Optional(static (scoped ref RlpReader r) => r.ReadString());
        });

        Assert.That(decoded, Is.EqualTo(value));
    }

    [Test]
    public void OptionalTrailingFields([Values(0, 1, 2)] int optionalCount)
    {
        (string, int, string?, int?) tuple = ("dog", 42, optionalCount > 0 ? "cat" : null, optionalCount > 1 ? 7 : null);

        byte[] rlp = Rlp.Write(tuple, static (ref RlpWriter w, (string _1, int _2, string? _3, int? _4) tuple) =>
        {
            w.Write(tuple._1);
            w.Write(tuple._2);
            if (tuple._3 is not null) w.Write(tuple._3);
            if (tuple._4.HasValue) w.Write(tuple._4.Value);
        });

        (string, int, string?, int?) decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            string _1 = r.ReadString();
            int _2 = r.ReadInt32();
            string? _3 = r.Optional(static (scoped ref RlpReader r) => r.ReadString());
            int? _4 = r.Optional(static (scoped ref RlpReader r) => r.ReadInt32());

            return (_1, _2, _3, _4);
        });

        Assert.That(decoded, Is.EqualTo(tuple));
    }

    // Optional only checks for the end of the input: a present item that does not decode must throw, not read as null.
    [Test]
    public void OptionalWithMalformedItem()
    {
        byte[] rlp = [0xc0];

        Assert.That(() => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.Optional(static (scoped ref RlpReader r) => r.ReadString())),
            Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void ChoiceDoesNotCatchOtherExceptions()
    {
        byte[] rlp = [0x2a];

        Assert.That(() => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.Choice(
                static (scoped ref RlpReader _) => throw new InvalidOperationException(),
                static (scoped ref RlpReader r) => r.ReadInt32())),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void UserDefinedRecord()
    {
        List<Student> students =
        [
            new("Ana", 23, new Dictionary<string, int>
            {
                { "Math", 7 },
                { "Literature", 9 }
            }),
            new("Bob", 25, new Dictionary<string, int>
            {
                { "Math", 9 },
                { "Literature", 6 }
            }),
        ];

        byte[] rlp = Rlp.Write(students, static (ref RlpWriter w, List<Student> students) =>
        {
            w.WriteSequence(students, static (ref RlpWriter w, List<Student> students) =>
            {
                foreach (Student student in students)
                {
                    w.Write(student);
                }
            });
        });

        List<Student> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
        {
            return r.ReadSequence(static (scoped ref RlpReader r) =>
            {
                List<Student> result = [];
                while (r.HasNext)
                {
                    result.Add(r.ReadStudent());
                }

                return result;
            });
        });

        Assert.That(decoded, Is.EqualTo(students).UsingPropertiesComparer());
    }

    [Test]
    public void ListCollection()
    {
        List<string> list = ["cat", "dog"];

        byte[] rlp = Rlp.Write(list, static (ref RlpWriter w, List<string> list) => w.Write(list, StringRlpConverter.Write));

        byte[] rlpExplicit = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.Write("cat");
                w.Write("dog");
            });
        });
        Assert.That(rlpExplicit, Is.EqualTo(rlp));

        List<string> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadList(StringRlpConverter.Read));

        Assert.That(list, Is.EqualTo(decoded));
    }

    [Test]
    public void ListOfListCollection()
    {
        List<List<string>> list = [
            ["dog", "cat"],
            ["foo"],
            []
        ];

        byte[] rlp = Rlp.Write(list, static (ref RlpWriter w, List<List<string>> list) =>
            w.Write(list, static (ref RlpWriter w, List<string> v) =>
                w.Write(v, StringRlpConverter.Write)));

        byte[] rlpExplicit = Rlp.Write(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) =>
            {
                w.WriteSequence(static (ref RlpWriter w) =>
                {
                    w.Write("dog");
                    w.Write("cat");
                });

                w.WriteSequence(static (ref RlpWriter w) =>
                {
                    w.Write("foo");
                });

                w.WriteSequence(static (ref RlpWriter _) =>
                {
                });
            });
        });
        Assert.That(rlpExplicit, Is.EqualTo(rlp));

        List<List<string>> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
            r.ReadList(static (scoped ref RlpReader r) =>
                r.ReadList(StringRlpConverter.Read)));

        Assert.That(list, Is.EqualTo(decoded));
    }

    [Test]
    public void DictionaryCollection()
    {
        Dictionary<int, string> dictionary = new()
        {
            { 1, "dog" },
            { 2, "cat" },
        };

        byte[] rlp = Rlp.Write(dictionary, static (ref RlpWriter w, Dictionary<int, string> dictionary) =>
            w.Write(dictionary, Int32RlpConverter.Write, StringRlpConverter.Write));

        byte[] rlpExplicit = Rlp.Write(dictionary, static (ref RlpWriter w, Dictionary<int, string> dictionary) =>
        {
            w.WriteSequence(dictionary, static (ref RlpWriter w, Dictionary<int, string> dictionary) =>
            {
                foreach (KeyValuePair<int, string> tuple in dictionary)
                {
                    w.WriteSequence(tuple, static (ref RlpWriter w, KeyValuePair<int, string> tuple) =>
                    {
                        w.Write(tuple.Key);
                        w.Write(tuple.Value);
                    });
                }
            });
        });
        Assert.That(rlp, Is.EqualTo(rlpExplicit));

        Dictionary<int, string> decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
            r.ReadDictionary(Int32RlpConverter.Read, StringRlpConverter.Read));

        Assert.That(decoded, Is.EqualTo(dictionary));
    }

    [Test]
    public void TupleCollection()
    {
        (int, int) tuple = (42, 1337);

        byte[] rlp = Rlp.Write(tuple, static (ref RlpWriter w, (int, int) tuple)
            => w.Write(tuple, Int32RlpConverter.Write, Int32RlpConverter.Write));

        byte[] rlpExplicit = Rlp.Write(tuple, static (ref RlpWriter w, (int, int) tuple) =>
        {
            w.Write(tuple.Item1);
            w.Write(tuple.Item2);
        });

        Assert.That(rlp, Is.EqualTo(rlpExplicit));

        (int, int) decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) =>
            r.ReadTuple(Int32RlpConverter.Read, Int32RlpConverter.Read));

        Assert.That(decoded, Is.EqualTo(tuple));
    }

    [Test]
    public void IntegerLimits()
    {
        using (Assert.EnterMultipleScope())
        {
            AssertRoundTrip(byte.MaxValue, static (scoped ref RlpReader r) => r.ReadByte());
            AssertRoundTrip(short.MaxValue, static (scoped ref RlpReader r) => r.ReadInt16());
            AssertRoundTrip(ushort.MaxValue, static (scoped ref RlpReader r) => r.ReadUInt16());
            AssertRoundTrip(int.MaxValue, static (scoped ref RlpReader r) => r.ReadInt32());
            AssertRoundTrip(uint.MaxValue, static (scoped ref RlpReader r) => r.ReadUInt32());
            AssertRoundTrip(long.MaxValue, static (scoped ref RlpReader r) => r.ReadInt64());
            AssertRoundTrip(ulong.MaxValue, static (scoped ref RlpReader r) => r.ReadUInt64());
            AssertRoundTrip(Int128.MaxValue, static (scoped ref RlpReader r) => r.ReadInt128());
            AssertRoundTrip(UInt128.MaxValue, static (scoped ref RlpReader r) => r.ReadUInt128());
            AssertRoundTrip(UInt256.MaxValue, static (scoped ref RlpReader r) => r.ReadUInt256());
            AssertRoundTrip(UInt256.Zero, static (scoped ref RlpReader r) => r.ReadUInt256());
            AssertRoundTrip(0UL, static (scoped ref RlpReader r) => r.ReadUInt64());
        }
    }

    // One byte more than the type holds, or a value that would be negative in a signed type.
    [TestCase("88ffffffffffffffff", TestName = "Int64 above MaxValue")]
    [TestCase("90ffffffffffffffffffffffffffffffff", TestName = "Int128 above MaxValue")]
    [TestCase("a1010000000000000000000000000000000000000000000000000000000000000000", TestName = "UInt256 of 33 bytes")]
    public void IntegerAboveLimit(string hex)
    {
        byte[] rlp = Convert.FromHexString(hex);

        Assert.That(() => rlp.Length switch
        {
            9 => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadInt64()).ToString(),
            17 => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadInt128()).ToString(),
            _ => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadUInt256()).ToString(),
        }, Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void StringWithInvalidUtf8()
    {
        byte[] rlp = [0x82, 0xc3, 0x28];

        Assert.That(() => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadString()), Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void DictionaryWithDuplicateKey()
    {
        byte[] rlp = Rlp.Write(static (ref RlpWriter w) => w.WriteSequence(static (ref RlpWriter w) =>
        {
            w.WriteSequence(static (ref RlpWriter w) => { w.Write(1); w.Write("dog"); });
            w.WriteSequence(static (ref RlpWriter w) => { w.Write(1); w.Write("cat"); });
        }));

        Assert.That(() => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadDictionary(Int32RlpConverter.Read, StringRlpConverter.Read)),
            Throws.TypeOf<RlpReaderException>());
    }

    private static void AssertRoundTrip<T>(T value, RefRlpReaderFunc<T> read)
    {
        byte[] rlp = Rlp.Write(value, static (ref RlpWriter w, T value) =>
        {
            switch (value)
            {
                case UInt256 uint256:
                    w.Write(uint256);
                    break;
                case byte b: w.Write(b); break;
                case short i16: w.Write(i16); break;
                case ushort u16: w.Write(u16); break;
                case int i32: w.Write(i32); break;
                case uint u32: w.Write(u32); break;
                case long i64: w.Write(i64); break;
                case ulong u64: w.Write(u64); break;
                case Int128 i128: w.Write(i128); break;
                case UInt128 u128: w.Write(u128); break;
            }
        });

        Assert.That(Rlp.Read(rlp, read), Is.EqualTo(value), typeof(T).Name);
    }
}

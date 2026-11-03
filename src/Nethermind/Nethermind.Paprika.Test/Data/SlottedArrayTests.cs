// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Data;

public class SlottedArrayTests
{
    private static readonly byte[] Data0 = Bytes.FromHexString("0x17");
    private static readonly byte[] Data1 = Bytes.FromHexString("0x1d1f");
    private static readonly byte[] Data2 = Bytes.FromHexString("0x2527");
    private static readonly byte[] Data3 = Bytes.FromHexString("0x1f29");
    private static readonly byte[] Data4 = Bytes.FromHexString("0x171819");

    [Test]
    public void Set_Get_Delete_Get_AnotherSet()
    {
        ReadOnlySpan<byte> key0 = Values.Key0.Span;

        Span<byte> span = stackalloc byte[SlottedArray.MinimalSizeWithNoData + key0.Length + Data0.Length];
        SlottedArray map = new(span);

        map.SetAssert(key0, Data0);

        map.GetAssert(key0, Data0);

        map.DeleteAssert(key0);
        map.GetShouldFail(key0);

        // should be ready to accept some data again
        map.SetAssert(key0, Data1, "Should have memory after previous delete");
        map.GetAssert(key0, Data1);
    }

    [Test]
    public void Bug()
    {
        SlottedArray map = new(stackalloc byte[Page.PageSize - 8]);

        NibblePath path0 = NibblePath.Parse("498FBC24196F969E81A7C5F8672622C2FE26549A3445F5F681EF66E5D9361234");
        Span<byte> data0 = [13];

        NibblePath path1 = NibblePath.Single(3, 1);
        Span<byte> data1 = [51];

        map.SetAssert(path0, data0);
        map.SetAssert(path1, data1);

        map.GetAssert(path0, data0);
        map.GetAssert(path1, data1);
    }

    [Test]
    public void Enumerate_all([Values(0, 1)] int odd)
    {
        SlottedArray map = new(stackalloc byte[256]);

        NibblePath key0 = NibblePath.Empty;
        NibblePath key1 = NibblePath.FromKey(Bytes.FromHexString("0x07")).SliceFrom(odd);
        NibblePath key2 = NibblePath.FromKey(Bytes.FromHexString("0x070d")).SliceFrom(odd);
        NibblePath key3 = NibblePath.FromKey(Bytes.FromHexString("0x070d1f")).SliceFrom(odd);
        NibblePath key4 = NibblePath.FromKey(Bytes.FromHexString("0x070d1f29")).SliceFrom(odd);

        map.SetAssert(key0, Data0);
        map.SetAssert(key1, Data1);
        map.SetAssert(key2, Data2);
        map.SetAssert(key3, Data3);
        map.SetAssert(key4, Data4);

        map.GetAssert(key0, Data0);
        map.GetAssert(key1, Data1);
        map.GetAssert(key2, Data2);
        map.GetAssert(key3, Data3);
        map.GetAssert(key4, Data4);

        using SlottedArray.Enumerator enumerator = map.EnumerateAll();

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, key0, Data0);

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, key1, Data1);

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, key2, Data2);

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, key3, Data3);

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, key4, Data4);

        Assert.That(enumerator.MoveNext(), Is.False);
    }

    [Test]
    public void Enumerate_nibble([Values(1, 2, 3, 4)] int nibble)
    {
        SlottedArray map = new(stackalloc byte[256]);

        NibblePath key0 = NibblePath.Empty;
        NibblePath key1 = NibblePath.FromKey(Bytes.FromHexString("0x1a"));
        NibblePath key2 = NibblePath.FromKey(Bytes.FromHexString("0x2a0d"));
        NibblePath key3 = NibblePath.FromKey(Bytes.FromHexString("0x3a0d1f"));
        NibblePath key4 = NibblePath.FromKey(Bytes.FromHexString("0x4a0d1f29"));

        map.SetAssert(key0, Data0);
        map.SetAssert(key1, Data1);
        map.SetAssert(key2, Data2);
        map.SetAssert(key3, Data3);
        map.SetAssert(key4, Data4);

        map.GetAssert(key0, Data0);
        map.GetAssert(key1, Data1);
        map.GetAssert(key2, Data2);
        map.GetAssert(key3, Data3);
        map.GetAssert(key4, Data4);

        NibblePath expected = nibble switch
        {
            1 => key1,
            2 => key2,
            3 => key3,
            4 => key4,
            _ => throw new Exception()
        };

        byte[] data = nibble switch
        {
            1 => Data1,
            2 => Data2,
            3 => Data3,
            4 => Data4,
            _ => throw new Exception()
        };

        using SlottedArray.NibbleEnumerator enumerator = map.EnumerateNibble((byte)nibble);

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, expected, data);

        Assert.That(enumerator.MoveNext(), Is.False);
    }

    [Test]
    public void Enumerate_2_nibbles([Values(1, 2, 3, 4)] int nibble0)
    {
        const byte nibble1 = 0xA;

        SlottedArray map = new(stackalloc byte[256]);

        NibblePath key0 = NibblePath.Empty;
        NibblePath key1 = NibblePath.FromKey([0x10 | nibble1]);
        NibblePath key2 = NibblePath.FromKey([0x20 | nibble1, 13]);
        NibblePath key3 = NibblePath.FromKey([0x30 | nibble1, 13, 31]);
        NibblePath key4 = NibblePath.FromKey([0x40 | nibble1, 13, 31, 41]);

        map.SetAssert(key0, Data0);
        map.SetAssert(key1, Data1);
        map.SetAssert(key2, Data2);
        map.SetAssert(key3, Data3);
        map.SetAssert(key4, Data4);

        map.GetAssert(key0, Data0);
        map.GetAssert(key1, Data1);
        map.GetAssert(key2, Data2);
        map.GetAssert(key3, Data3);
        map.GetAssert(key4, Data4);

        NibblePath expected = nibble0 switch
        {
            1 => key1,
            2 => key2,
            3 => key3,
            4 => key4,
            _ => throw new Exception()
        };

        byte[] data = nibble0 switch
        {
            1 => Data1,
            2 => Data2,
            3 => Data3,
            4 => Data4,
            _ => throw new Exception()
        };

        using SlottedArray.Nibble2Enumerator enumerator = map.Enumerate2Nibbles((byte)nibble0, nibble1);

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, expected, data);

        Assert.That(enumerator.MoveNext(), Is.False);
    }

    [Test]
    public void Enumerate_long_key([Values(0, 1)] int oddStart, [Values(0, 1)] int lengthCutOff)
    {
        SlottedArray map = new(stackalloc byte[128]);

        NibblePath key = NibblePath.FromKey(Keccak.EmptyTreeHash).SliceFrom(oddStart)
            .SliceTo(NibblePath.KeccakNibbleCount - oddStart - lengthCutOff);

        map.SetAssert(key, Data0);
        map.GetAssert(key, Data0);

        using SlottedArray.Enumerator enumerator = map.EnumerateAll();

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, key, Data0);

        Assert.That(enumerator.MoveNext(), Is.False);
    }

    [Test]
    public void Set_Get_Empty()
    {
        ReadOnlySpan<byte> key0 = Values.Key0.Span;

        SlottedArray map = new(stackalloc byte[128]);

        ReadOnlySpan<byte> data = ReadOnlySpan<byte>.Empty;

        map.SetAssert(key0, data);
        map.GetAssert(key0, data);

        map.DeleteAssert(key0);
        map.GetShouldFail(key0);

        // should be ready to accept some data again
        map.SetAssert(key0, data, "Should have memory after previous delete");
        map.GetAssert(key0, data);

        using SlottedArray.Enumerator enumerator = map.EnumerateAll();

        Assert.That(enumerator.MoveNext(), Is.True);
        AssertItem(enumerator.Current, NibblePath.FromKey(key0), data);

        Assert.That(enumerator.MoveNext(), Is.False);
    }

    private const int DefragmentationKeyCount = 27;

    [TestCase(0, TestName = "0th")]
    [TestCase(1, TestName = "1st")]
    [TestCase(2, TestName = "2nd")]
    [TestCase(DefragmentationKeyCount - 1, TestName = "last")]
    public void Defragmentation(int deleted)
    {
        // The size found by running the test multiple times.
        SlottedArray map = new(stackalloc byte[1330]);

        Random random = new(13);

        // Init values
        Keccak[] keys = new Keccak[DefragmentationKeyCount];
        byte[][] data = new byte[DefragmentationKeyCount][];

        for (int i = 0; i < DefragmentationKeyCount; i++)
        {
            keys[i] = random.NextKeccak();
            data[i] = new byte[i]; // make them vary by length
            random.NextBytes(data[i]);

            map.SetAssert(keys[i].Span, data[i], $"at {i}th iteration");
        }

        // Additional data
        Keccak additionalKey = random.NextKeccak();
        byte[] additionalData = [random.NextByte()];

        // Ensure that one needs to be deleted
        if (map.TrySet(NibblePath.FromKey(additionalKey), additionalData))
        {
            Assert.Fail("There should be no space in the map to make the defragmentation test work");
        }

        if (!map.Delete(NibblePath.FromKey(keys[deleted])))
        {
            Assert.Fail("The key should have existed before");
        }

        // Now insertion should be possible
        Assert.That(map.TrySet(NibblePath.FromKey(additionalKey), additionalData), Is.True);

        for (int i = 0; i < DefragmentationKeyCount; i++)
        {
            if (i != deleted)
            {
                map.GetAssert(keys[i].Span, data[i]);
            }
        }

        map.GetAssert(additionalKey.Span, additionalData);
    }

    [Test]
    public void DefragmentationLarge()
    {
        // Create random data to insert in the slotted array.
        const int maxKeyCount = 200;
        Random random = new(13);
        Keccak[] keys = new Keccak[maxKeyCount];
        byte[][] data = new byte[maxKeyCount][];

        for (int i = 0; i < maxKeyCount; i++)
        {
            keys[i] = random.NextKeccak();
            data[i] = new byte[i]; // make them vary by length
            random.NextBytes(data[i]);
        }

        // Additional data to trigger defragmentation.
        Keccak additionalKey = random.NextKeccak();
        byte[] additionalData = [random.NextByte()];

        // Try all possible deleteCount values and ensure that the defragmentation works fine.
        for (int deleteCount = 1; deleteCount < maxKeyCount; deleteCount++)
        {
            SlottedArray map = new(new byte[8000]);

            // Fill as many elements as possible within the slotted array.
            int keyCount = 0;

            for (int i = 0; i < maxKeyCount; i++)
            {
                if (!map.TrySet(NibblePath.FromKey(keys[i]), data[i]))
                {
                    keyCount = i;
                    break;
                }
            }

            // Estimated number of keyCount based on the buffer length.
            Assert.That(keyCount, Is.GreaterThanOrEqualTo(96), "Unexpected number of keys");

            if (deleteCount > keyCount)
            {
                // Exhausted all the valid deleteCount values.
                break;
            }

            // Ensure that one needs to be deleted
            if (map.TrySet(NibblePath.FromKey(additionalKey), additionalData))
            {
                Assert.Fail("There should be no space in the map to make the defragmentation test work");
            }

            // Randomly select keys to be deleted.
            HashSet<int> deletedKeys = new(deleteCount);

            for (int i = 0; i < deleteCount; i++)
            {
                int keyIndex = random.Next(keyCount);

                while (!deletedKeys.Add(keyIndex))
                {
                    keyIndex = random.Next(keyCount);
                }

                if (!map.Delete(NibblePath.FromKey(keys[keyIndex])))
                {
                    Assert.Fail("The key should have existed before");
                }
            }

            // Now insertion should be possible
            Assert.That(map.TrySet(NibblePath.FromKey(additionalKey), additionalData), Is.True);

            for (int i = 0; i < keyCount; i++)
            {
                if (!deletedKeys.Contains(i))
                {
                    map.GetAssert(keys[i].Span, data[i]);
                }
            }

            map.GetAssert(additionalKey.Span, additionalData);
        }
    }

    [Test]
    public void Defragmentation_bug()
    {
        SlottedArray map = SlottedArray.LoadFromDump(
            "7801881d02000000ffff8bd400190014302c353c3f3c0012602e8bcf902c9b1c102e001ddc1d0016b83f50ffcd7e2a7ee87d80fd627d7f7c17fcaffb917b29fb0b7b887a20fa5d79dedf8c3989e90018cc5f001fc02f89d98939895989a9302e001e303e60be606ef5f8d7786ff84c77e4f6e175c3755bf559753cb51fb501753e74d6f3b9b351f360ee429417f44024102430243e04802460246ad48c39a029a6a900290d39286934b3ccf264f2467228720a72a2f184716671fef096f0787010f0f26f8aef22ef20298029ffe5a8dc099c002ca02c3fac353ca34d0a7d002da02d902d9fadd02d046fc26e7f2d17edafec916c736c0beced6b85eb1debff6ae16ac36a5bea3d6ab4ee9c3e902eb02ef02efbfe105e602e603e475f6e5f402fb02fbc0f633f6e3f18eab0e9926974695669eee886e88368816819e8b1e7936775670de7ef66d166633f602f6fcf6f3f225f202f3cdf302f802fd02f6adc602c001c0010001c001b69e60766e2e5c4655ce53e65d6e4b8649a647c6414e4f66353633062cd617d62e7a4ffe6f2c6dda0ffe0dfd050a950c9ff9e4a7296e36bf29d25ef5bda0b5749d42efb7395e63bee40036072546c72d436df9ff6e98f84f70c88802009147943ab78e8c8b0787d0db004045f6100490648c2edac468b2790e108f7c7e197a93bd80df00424cd51f36184d31b283aca70176730cbb89962b5a74d7f1b77ba39cb58d4a3ba34d9d7a7991d597ba44b1d321931acfbb83997f82bdc280e18655542824cc72ace5b410d4f5f0ecbfeac2322007b895b9dbc6037a0a4d711ba51780b00bf0b47a717f967c9ecd45f42144112cc32a411fa5bd8c47f1ea18c665cc55b47ce170a11ced61ef333a3c2c3ab09881b1af3883362e9307e97840388fae664593792d057ca9642e864830a8aade324b81c0dd18a19cd555bbf41e26f5721a7e0c314c1d6852dcfe8337d9e5e535c3173a16af047aa0078e2245c2607349ddcb3ef7ad4d50f8bd9b7fd5a2c20e988e30bd0bed3e73fafc0c029b40181a4a7eb99a22449a50aafb74c96748ef880119b3f757375de1dd9450b36e6970494ece083188c3e516a22766432661fd96aa2151a88bf6ee5bf8849806d2d9098bd4135dbcfdb61e5de2958585602a2c4354b15370fe319a76f3c66773635fb727f85fcc42c721fe66cad66e3758e28a518cd96b8dfc800490648c2edac468b2790e108f7c7e197a93bd80df00424cd51f36184d31b283aca70176730cbb89962b5a74d7f1b77ba39cb58d4a3ba34d9d7a7991d597ba44b1d321931acfbb83997f82bdc280e18655542824cc72ace5b410d4f5f0ecbfeac2322205d7a6936cb8531ad6d27454012b09d7950cc76bfde56880496401d43bdbc09550603272bf202c67394b1e6dd764ab9c976e7a330dcbe69c7d078c8d67dfe3f9abf4f4f0f59f6e0bd7cf3550954fbcccd70ab3b86a73024ec186b0f58ed72bf4f4f0f59f6e0bd7cf3550954fbcccd70ab3b86a73024ec186b0f58e004045a46540b5a46540bc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219e0bbc86f405f600e966a36ca13aa927149524a9552b09bc82558a8fea5d9bb3898a03ea1968c72be7eaf1caa712ea0deec17af0980378a99936f1fc9c581289c71f7b9e15871d9e96f520038ad8b46a620c6ab6187fed1e892d72581289c71f7b9e15871d9e96f520038ad8b46a620c6ab6187fed1e892004042849491128494911c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219204c50ff450b8864924074304a9fca25ac5c970821beb05499f74c99d857204c50ff450b8864924074304a9fca25ac5c970821beb05499f74c99d80040445b0d28a45b0d28ac5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42180613c9612d6dfe7badec7c7d80ea03f6d853f4a132761f1a36389a0ec1c72613c9612d6dfe7badec7c7d80ea03f6d853f4a132761f1a36389a0ec10b383e21883e21833fe354e44292cc8a2b8c8c332cf0df23a108bb2a8ef6b94f7bab68cfc69c1727cade1d7a32d0e17e8b039c53e67673f272ebbde87147db3dbafe229a61b434c537687aa1b1843a48e1cc4031cd2582a966971f4886bb898bb097577b1b57f32d9dc720ffbdc6a778bd09cb15d3853e4882685f9cf6f6ca77490c43be6b59c20040446cdb69e46cdb69ec5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42180c22bc50012cba127e4f264f0aae1ce24de60b33c739c004957bde0c7d5800ffbdc6a778bd09cb15d3853e4882685f9cf6f6ca77490c43be6b59c2372c29dcaa75be414aebd8ac8b7b79cdb133f2568f56706f6f1ecd663e0b00404643ae6fc643ae6fcc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219cc29dcaa75be414aebd8ac8b7b79cdb133f2568f56706f6f1ecd663e0b097d35d9f1fcf5c868a8fb20f5a239c0d98b65d2b6ebc65886a9219431fd572c22bc50012cba127e4f264f0aae1ce24de60b33c739c004957bde0c7d0040424967bc624967bc6c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42172d35d9f1fcf5c868a8fb20f5a239c0d98b65d2b6ebc65886a9219431fd004043c4c2e533c4c2e53c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4213bee40036072546c72d436df9ff6e98f84f70c88802009147943ab78e8c8b0787d0db004045f6142c45f6142c4c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4217247dd4e4a7544d15faa007defab535c7ce58c2c402955f97ef2ed928a5004042375c1812375c181c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219b47dd4e4a7544d15faa007defab535c7ce58c2c402955f97ef2ed928a5f9481cc56f3e612670057c5b2522a54d8edee3d7f533f93a5041ebbe6869e9c8a1dac03a4a4fb0a9873a7a47a2b992b4ec07ea8320468d2d2d8ab8713728a1dac03a4a4fb0a9873a7a47a2b992b4ec07ea8320468d2d2d8ab87100404147fe2ee147fe2eec5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4217281cc56f3e612670057c5b2522a54d8edee3d7f533f93a5041ebbe68690b3e54e34e54e349c06449d84ef49ab3fe9ba4609d5ba85329fa56100808626e2577adc2d9172eca3d56a192cdec34654487d9aa17072edfce77ea6f32831d99355da20040402cc9b0d02cc9b0dc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219feca3d56a192cdec34654487d9aa17072edfce77ea6f32831d99355da2a93fbfbe5b55a93899c38eb80cb27d082daa074e139e646f4334282752ae49a54d3adf14ae80c43b3ef9b31730795e51b153d8c9890b0658b186396277254d3adf14ae80c43b3ef9b31730795e51b153d8c9890b0658b1863962004042bc856f82bc856f8c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42172fbfbe5b55a93899c38eb80cb27d082daa074e139e646f4334282752ae004045b9aaba35b9aaba3c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42180cb1ab037075dce1d4268abe42f0c8b9845d9dd12275e6cd91a1daacc637204b9038819104a9d61a779c4c629a1238cef412c5ea15f5f201493be7004044f4bdf874f4bdf87c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42198c88ed6068a0687c9b3582ec4e2aff4ccf5c2899211a2eff260f9de477d990fd9b0ac7c67dce1c809acbe45d356c595c8847bfa232b45f305462699720fd9b0ac7c67dce1c809acbe45d356c595c8847bfa232b45f3054626900404076a1c35076a1c35c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42172c88ed6068a0687c9b3582ec4e2aff4ccf5c2899211a2eff260f9de4770040403a7640f03a7640fc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421009bdc65e150ad36d8eaec51694d3240afd4c1908b90a4a792153a8bf72c0f54c368f2fa3f734b99bb107f7c00a272570efb6e0e84843a200df9a96378cf2d97e927cf0b3243ab8e2e9ecbaf0d4f3f54ad18e33e5d784ec99cbade13a45bc88390ce471c81bc5ec013c36961bf4dc1fefa2d023e676ae399510175b149fd88fdacc67fb137a4c0d6d108fd4e1a78332dc0657b1058d24689f42991f89de559792cdb5a6681b5c214942fc56a0dbe96d89e1efe306a8bced101abe9cce16e94489634f75dc744d9e9789677dba1437f2671c33b1c79a479928a20c7245e94a716926c84e59511d6eea30a1cc270c11cb3093988d751b674b5369d0a14ba01f6ebbbe1c3445bfe89eb4e823e9f579c0360a264f1a99ba02c4c9deb5c554605258485b7532990738d6fd4e7a443a1e0458248ecced1aa7a3cf0135203229ba49bf014857b39cc0263bbb3a5335d74ee6926236059ae4d32bee4b43cd4bfc766d657af40300d906f85f3c7043eea45edb9c444979fb6e99bdc9ce3d6c560a3a399de6fbf4e8cbf989bc786452966adad7716f4df1c00624ef6c42030bd5e595a315afa4ed6729bc786452966adad7716f4df1c00624ef6c42030bd5e595a315afa4ed00404181e39cf181e39cfc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421725cad6695a75d513357829194ef91a06b6557e86f9edd56bcd40a9f5a9004045d2978915d297891c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219d5cad6695a75d513357829194ef91a06b6557e86f9edd56bcd40a9f5a9372e9c160d184b9091445442545c39836289abf6e812c50c662992fa8f6a004047abe55d87abe55d8c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42196e9c160d184b9091445442545c39836289abf6e812c50c662992fa8f6aa72aac12cb6315edc38123a7425da6868629613d5248fa759bfd85adda9f00404507e930f507e930fc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4217213d59669b6e5daedd20eb13ba061cd73f7947ff23d0fbf0f2fbffb6de004045b1fb3b55b1fb3b5c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219a13d59669b6e5daedd20eb13ba061cd73f7947ff23d0fbf0f2fbffb6ded9bb2c7c1c834df3d8c8daac9a14f660a627068065af79293d0157459744d72517654f81ed3728044229b9c2c2a079fdbb9abd151f15f4841a9be8550040460f4386e60f4386ec5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219e517654f81ed3728044229b9c2c2a079fdbb9abd151f15f4841a9be8550976c012929b6b5d6fd8d233565b5b91faae9f24ff39b223c752fe506477f926e601443251913cc3e1bb07757eb8368add4d2389f1836887418876df9726c012929b6b5d6fd8d233565b5b91faae9f24ff39b223c752fe5064770040416fbec6b16fbec6bc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421726e601443251913cc3e1bb07757eb8368add4d2389f1836887418876df0040476cbcace76cbcacec5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42191fdfd65f7bd2cc89f8be33490726758cd9c57c5aa68a67b156603d45272b81b54e7d76dbfb2c189d3243fbbae937a43fd97a1cb1962de6dccfbc004045c6db2ec5c6db2ecc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421981b54e7d76dbfb2c189d3243fbbae937a43fd97a1cb1962de6dccfbc6723403d2023ee72a950ffbd2c4ec37848171ace9c9fadad91315fe9f62c004041a5b380d1a5b380dc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421004a8ab7c316ae3abdd0bce06646caf2d8fd99386831c8e87ce93dac1976941ee947dd315631d5554c75e6a325b4de9df2c4d74cea47fbc167691592c7d33f7debc3f2edffc84dab4ecf1fef704750dc8fb6d052bfc3c91fff9eeb6a08cf2c5a3dbcb19303df85cbbcb70eca45cef7492160e6cea70a167e0757a1b969c4f7d02c8e048c7c7e35ad63dd864ce04f84890f88118d4eb5d7513a334284dbac8f693d1c17996325e5fa43c2b031d0aa10cd7a359af8c74f93080c790536068367d0e419a1903403d2023ee72a950ffbd2c4ec37848171ace9c9fadad91315fe9f62c399440f0088dc960264f6a596135f82a4a5429da815a0495d14dff7e6bd9a4dc6b4a1b28676b7297332ae061588ede20c584aae6d8fc4c645393e35aa72a9440f0088dc960264f6a596135f82a4a5429da815a0495d14dff7e6b0040465df37ee65df37eec5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219cc98a85f2eba57fa17c35e9d6a2a99d458ea1d1d14df58cc6cf098461b5005c39cedec2e331a0f140f243056450aeb7186014482d832ce3e2a45d1514a7f2b3586bcd5ea3866d32954c2bf8fcd440228a222d236967a1c364aba87926d7be336081469ca5c8b6bbe613453cb34d246dd5b4df3ff65a5ffdb6ac1408a1ddc6d9a136f9de40d0d6c215e29428ec514f3a2a84919fce712588f83afc1253045c555bd5d5ef4bccfcf69d236c06219b5e62a8109e0f5237a825bb36aa33de70d46102303a641fb67f20587a486124010cc8c613b7e4dd9eea7126e32a95fc87e72a9d7a26f3c4f6217b4320f8869448c027f787500ff9f9e1cc56237860e410fd82c28b7542e0c7eeacc4dc4e1cd31c06c510515567aeaffe0147b2cc1d8ac95315d272c98a85f2eba57fa17c35e9d6a2a99d458ea1d1d14df58cc6cf098461b004042d8665522d866552c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421003e56603f6f8a5cc797aac091480a9a48f83cbc5edbe26e1d7a4cdd5b50b2ac1bbb348f2b98219e11827a04cf8681e01fcc6d556b74d7ca974c733ce3adc813383b9d053b97ebdcd7d66adb6525d54b8716dbf7bec379277acf40c8f6a918d75783ba29db2ad8c2f99ebcf3d457b18532f0b0cc6c5d98abe38c3b9cd6fdc1135b059237ca619c42e968c4f792788ff693407d3217f49d95121ce88dbb86a5c66ee41fc90b431890d58d8925a2ba86b2693f497da978ed6231840c1b214f4f15441d1921ec2ebbffe06debb3a1e9cc88111a189ceb5fd3a8fe9be57346db627b508167256a9025206da6f94bf48a5023f818bd41bd3e77dbc5acafd4dac2ee06348dad4ad6c208bb10abae26c84bda9e9606ff7e96e609bd80db70eb5086f017adb39b725a4dc6b4a1b28676b7297332ae061588ede20c584aae6d8fc4c645393004043e55f6553e55f655c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42180aac12cb6315edc38123a7425da6868629613d5248fa759bfd85adda9f3720bbc86f405f600e966a36ca13aa927149524a9552b09bc82558a8fea50040467620b8367620b83c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421004455f3e60d87efb0c86dd351bf6eacaedf5942f185d3fed8d4f8159f4c54e61bf89094c3cd1eab022f4f2bb634a09fba81c84b2bbfa41fa5e238b970ffc2db6dc00c1543a6d888da0199b95bf0c7b5a9c11f39a71cce3e307287229128d821acfa9b507dc4b63ebdbd2e550aa26532ea6708870067d3cb3558d915c48723fd9b76168bc1ff3d11e82f3dd160245a811d41c47189b0a770ec29c96ca3af9d20e0c7b58a15a903e8de11842b57a64c54c7b0007a526e377294c8aff29424d8ca1a055c7206449d84ef49ab3fe9ba4609d5ba85329fa56100808626e2577adc2d9004045cea95725cea9572c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4210001261b2734885ff390b54a64126b4f3eec24503706acb6b8150d2acbca05332c4afa1e0cb66f4e723cfe7a99445fe7c06f6606ac11ba7e026656f991f367ceb07700b3e7bcc1c31fb7303691cf23159be96b5fd7bf35472501841e7d25ebde596b2de955a6dfe6fbdf69a4ff72ab0516fb1cea2b45eaebfef1210feaffa6df64109390546c72d436df9ff6e98f84f70c88802009147943ab78e8c8b0787d0db572d5c2d4ec4bfd43373528d2c70a0a28d4062244a0c0fe1965e70b4f6090040467400bd367400bd3c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4219bd5c2d4ec4bfd43373528d2c70a0a28d4062244a0c0fe1965e70b4f609172b3898a03ea1968c72be7eaf1caa712ea0deec17af0980378a99936f1f00404221bc027221bc027c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42172e1fdfd65f7bd2cc89f8be33490726758cd9c57c5aa68a67b156603d450040472136c9572136c95c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42100d0e205245529cabe6ce34bbdcc5d5d1b49e747fc5dace5cd609a8d31036f5c55a6466e7501e41f710b96ca853f9ef3d6fcbd766c5b8af1ae2404d615f414988139c98a3e8c84a6b7b8483eba3ebb78d70a177b677cf3745560bdb9964e66feb121dad30117e836e436242c15fd42b5fd0468dd665ee773662b707a7cc0857a9b52f9a3cb18e737101ed6aca28e9ec7e0f09bdebad74b55040b0add2333fc2fb8176c74bbd70f1debfcc4cf2f834a3b3f4aa60720e0519e7e4e9c814c3724a1e57decd925a57bd532cc39f016836d392b9ae90fb8f4a60d09dc10418624d9165f69c78004b9038819104a9d61a779c4c629a1238cef412c5ea15f5f201493be7a72cb1ab037075dce1d4268abe42f0c8b9845d9dd12275e6cd91a1daacc6004045336091353360913c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b42135ffc4d1b93882cd4ab3c1ac53209405ed5a2b90a10102df27a8e34f44bed2f64a620820d85415da88a79f0ae73e213cbf3cfb1ccff52fcb17477d2a2292699e2821005a01c59fa2330aa6dd6279b10eaf5220ce95a36ea5658fe57782a44d2d0e8546278c744f108cb386b2f2e89f1daf8f5d6a71fe889b4d1e1cedd1116c6151f47a3b5de7396845415250f10e3e192866754ea7b15f52ff70a7a015247c47174920aedfc7aeff64d84cb43043487fc12a4db924553495849847c99d3e2b10a0ea55099bdeed88c557943b7dbe04b57c02aec077117d7a75f02682ecbaae3ee82cb219580005052aae9689cd299769aaa5bdf316e51745cd74391206435e34b4a79ed6d8d8e8e664e35e18f874cf65255b8688cd66fdcc470d12a9714edbfd95e0cf836b97ea1002b3f2efb0a3a3e533fb784ee76030f74bf98bb3598f700a287b563404ca658bfe5ee40554ebb9526d6b56bfe0f053002a371a34f6dd8260f2d6886db3b43f0e72b2c7c1c834df3d8c8daac9a14f660a627068065af79293d015745974400404340869d2340869d2c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a47056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421");

        map.SetAssert(NibblePath.Single(0xA, 1), new byte[355]);
    }

    [Test]
    public void Update_in_situ()
    {
        // by trial and error, found the smallest value that will allow to put these two
        SlottedArray map = new(stackalloc byte[128]);

        ReadOnlySpan<byte> key1 = Values.Key1.Span;

        map.SetAssert(key1, Data1);
        map.SetAssert(key1, Data2);

        map.GetAssert(key1, Data2);
    }

    [Test]
    public void Update_in_resize()
    {
        ReadOnlySpan<byte> key0 = Values.Key0.Span;

        // Update the value, with the next one being bigger.
        SlottedArray map = new(stackalloc byte[SlottedArray.MinimalSizeWithNoData + key0.Length + Data0.Length]);

        map.SetAssert(key0, Data0);
        map.SetAssert(key0, Data2);

        map.GetAssert(key0, Data2);
    }

    [Test]
    public void Small_keys_compression()
    {
        SlottedArray map = new(stackalloc byte[512]);

        Span<byte> key = stackalloc byte[1];
        Span<byte> value = stackalloc byte[2];

        const int count = 34;

        for (byte i = 0; i < count; i++)
        {
            key[0] = i;
            value[0] = i;
            value[1] = i;

            map.SetAssert(key, value, $"{i}th was not set");
        }

        for (byte i = 0; i < count; i++)
        {
            key[0] = i;
            value[0] = i;
            value[1] = i;

            map.GetAssert(key, value);
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Key_of_length_5(int odd)
    {
        const int length = 5;

        // One should be enough as the leftover path of length 1 should be encoded as a single byte
        const int spaceForKey = 1;

        SlottedArray map = new(stackalloc byte[SlottedArray.MinimalSizeWithNoData + spaceForKey]);

        NibblePath key = NibblePath.FromKey(Bytes.FromHexString("0x34057a"), odd, length);

        ReadOnlySpan<byte> value = ReadOnlySpan<byte>.Empty;
        map.SetAssert(key, value);
        map.GetAssert(key, value);
    }

    [Test]
    public void Key_of_length_6_even()
    {
        const int length = 6;

        // One should be enough as the leftover path of length 1 should be encoded as a single byte
        const int spaceForKey = 1;

        SlottedArray map = new(stackalloc byte[SlottedArray.MinimalSizeWithNoData + spaceForKey]);

        // 0b10 is the prefix of the nibble that can be densely encoded on one byte.
        NibblePath key = NibblePath.FromKey(stackalloc byte[] { 0x34, 0b1001_1101, 0x7A }, 0, length);

        ReadOnlySpan<byte> value = ReadOnlySpan<byte>.Empty;
        map.SetAssert(key, value);
        map.GetAssert(key, value);
    }

    [Test]
    public void Key_of_length_6_odd()
    {
        const int length = 6;

        // One should be enough as the leftover path of length 1 should be encoded as a single byte
        const int spaceForKey = 1;

        SlottedArray map = new(stackalloc byte[SlottedArray.MinimalSizeWithNoData + spaceForKey]);

        // 0b10 is the prefix of the nibble that can be densely encoded on one byte. For odd, first 3 are consumed to prepare.
        NibblePath key = NibblePath.FromKey(stackalloc byte[] { 0x04, 0b1011_0010, 0xD9, 0x7A }, 0, length);

        ReadOnlySpan<byte> value = ReadOnlySpan<byte>.Empty;
        map.SetAssert(key, value);
        map.GetAssert(key, value);
    }

    [Test(Description = "Make a lot of requests to make breach the vector count")]
    public void Breach_VectorSize_with_key_count()
    {
        const int seed = 13;
        Random random = new(seed);
        Span<byte> key = stackalloc byte[4];

        SlottedArray map = new(new byte[3 * 1024]);

        const int count = 257;

        for (int i = 0; i < count; i++)
        {
            random.NextBytes(key);
            map.SetAssert(key, [(byte)(i & 255)]);
        }

        // reset
        random = new(seed);
        for (int i = 0; i < count; i++)
        {
            random.NextBytes(key);
            map.GetAssert(key, [(byte)(i & 255)]);
        }
    }

    [Test(Description = "Make a lot of requests to make breach the vector count")]
    public void Set_Get_With_Specific_Lengths([Values(8, 16, 32, 64, 68, 72)] int count)
    {
        const int keyLength = 2;

        Span<byte> keys = stackalloc byte[count * 2];
        for (byte i = 0; i < count; i++)
        {
            keys[i * keyLength] = i;
            keys[i * keyLength + 1] = i;
        }

        SlottedArray map = new(new byte[3 * 1024]);

        for (int i = 0; i < count; i++)
        {
            map.SetAssert(GetKey(keys, i), GetValue(i));
        }

        for (int i = 0; i < count; i++)
        {
            map.GetAssert(GetKey(keys, i), GetValue(i));
        }

        return;

        static NibblePath GetKey(Span<byte> keys, int i) => NibblePath.FromKey(keys.Slice(i * keyLength, keyLength));
        static ReadOnlySpan<byte> GetValue(int i) => new byte[(byte)(i & 255)];
    }

    private static ReadOnlySpan<byte> Data(byte key) => new[] { key };

    [TestCase(new[] { 1 })]
    [TestCase(new[] { 2, 4 })]
    [TestCase(new[] { 0, 1, 7 })]
    public void Remove_keys_from(int[] indexes)
    {
        SlottedArray toRemove = new(stackalloc byte[512]);
        SlottedArray map = new(stackalloc byte[512]);

        NibblePath key1 = NibblePath.Parse("1");
        NibblePath key2 = NibblePath.Parse("23");
        NibblePath key3 = NibblePath.Parse("345");
        NibblePath key4 = NibblePath.Parse("4567");
        NibblePath key5 = NibblePath.Parse("56789");
        NibblePath key6 = NibblePath.Parse("56899A");
        NibblePath key7 = NibblePath.Parse("56899AB");
        NibblePath key8 = NibblePath.Parse("56899AB1");

        // Set receiver with all the keys
        map.SetAssert(NibblePath.Empty, Data(0));
        map.SetAssert(key1, Data(1));
        map.SetAssert(key2, Data(2));
        map.SetAssert(key3, Data(3));
        map.SetAssert(key4, Data(4));
        map.SetAssert(key5, Data(5));
        map.SetAssert(key6, Data(6));
        map.SetAssert(key7, Data(7));
        map.SetAssert(key8, Data(8));

        foreach (int index in indexes)
        {
            NibblePath removed = index switch
            {
                0 => NibblePath.Empty,
                1 => key1,
                2 => key2,
                3 => key3,
                4 => key4,
                5 => key5,
                6 => key6,
                7 => key7,
                8 => key8,
                _ => default
            };
            toRemove.SetAssert(removed, ReadOnlySpan<byte>.Empty);
            Assert.That(map.Contains(removed), Is.True);
        }

        map.RemoveKeysFrom(toRemove);

        // Assert non existence
        foreach (int index in indexes)
        {
            NibblePath removed = index switch
            {
                0 => NibblePath.Empty,
                1 => key1,
                2 => key2,
                3 => key3,
                4 => key4,
                5 => key5,
                6 => key6,
                7 => key7,
                8 => key8,
                _ => default
            };
            Assert.That(map.Contains(removed), Is.False);
        }
    }

    [Test]
    public void Move_to_1()
    {
        SlottedArray original = new(stackalloc byte[256]);
        SlottedArray copy0 = new(stackalloc byte[256]);

        NibblePath key1 = NibblePath.Parse("1");
        NibblePath key2 = NibblePath.Parse("23");
        NibblePath key3 = NibblePath.Parse("345");
        NibblePath key4 = NibblePath.Parse("4567");
        NibblePath key5 = NibblePath.Parse("56789");

        original.SetAssert(NibblePath.Empty, Data(0));
        original.SetAssert(key1, Data(1));
        original.SetAssert(key2, Data(2));
        original.SetAssert(key3, Data(3));
        original.SetAssert(key4, Data(4));
        original.SetAssert(key5, Data(5));

        original.MoveNonEmptyKeysTo(new MapSource(copy0));

        // original should have only empty
        Assert.That(original.Count, Is.EqualTo(1));
        original.GetAssert(NibblePath.Empty, Data(0));
        original.GetShouldFail(key1);
        original.GetShouldFail(key2);
        original.GetShouldFail(key3);
        original.GetShouldFail(key4);
        original.GetShouldFail(key5);

        // copy should have all but empty
        Assert.That(copy0.Count, Is.EqualTo(5));
        copy0.GetShouldFail(NibblePath.Empty);
        copy0.GetAssert(key1, Data(1));
        copy0.GetAssert(key2, Data(2));
        copy0.GetAssert(key3, Data(3));
        copy0.GetAssert(key4, Data(4));
        copy0.GetAssert(key5, Data(5));
    }

    [Test]
    public void Move_to_SlottedArray()
    {
        SlottedArray original = new(stackalloc byte[256]);
        SlottedArray copy0 = new(stackalloc byte[256]);

        NibblePath key1 = NibblePath.Parse("1");
        NibblePath key2 = NibblePath.Parse("23");
        NibblePath key3 = NibblePath.Parse("345");
        NibblePath key4 = NibblePath.Parse("4567");
        NibblePath key5 = NibblePath.Parse("56789");

        original.SetAssert(NibblePath.Empty, Data(0));
        original.SetAssert(key1, Data(1));
        original.SetAssert(key2, Data(2));
        original.SetAssert(key3, Data(3));
        original.SetAssert(key4, Data(4));
        original.SetAssert(key5, Data(5));

        original.MoveNonEmptyKeysTo<NibbleSelector.All>(copy0);

        // original should have only empty
        Assert.That(original.Count, Is.EqualTo(1));
        original.GetAssert(NibblePath.Empty, Data(0));
        original.GetShouldFail(key1);
        original.GetShouldFail(key2);
        original.GetShouldFail(key3);
        original.GetShouldFail(key4);
        original.GetShouldFail(key5);

        // copy should have all but empty
        Assert.That(copy0.Count, Is.EqualTo(5));
        copy0.GetShouldFail(NibblePath.Empty);
        copy0.GetAssert(key1, Data(1));
        copy0.GetAssert(key2, Data(2));
        copy0.GetAssert(key3, Data(3));
        copy0.GetAssert(key4, Data(4));
        copy0.GetAssert(key5, Data(5));
    }

    [Test]
    public void Move_to_respects_tombstones()
    {
        const int size = 256;

        SlottedArray original = new(stackalloc byte[size]);
        SlottedArray copy0 = new(stackalloc byte[size]);

        NibblePath key1 = NibblePath.Parse("1");
        NibblePath key2 = NibblePath.Parse("23");
        NibblePath key3 = NibblePath.Parse("345");
        NibblePath key4 = NibblePath.Parse("4567");
        NibblePath key5 = NibblePath.Parse("56789");

        ReadOnlySpan<byte> tombstone = ReadOnlySpan<byte>.Empty;

        original.SetAssert(key1, tombstone);
        original.SetAssert(key2, tombstone);
        original.SetAssert(key3, tombstone);
        original.SetAssert(key4, tombstone);
        original.SetAssert(key5, Data(5));

        original.MoveNonEmptyKeysTo(new MapSource(copy0), true);

        // original should have only empty
        using (Assert.EnterMultipleScope())
        {
            Assert.That(original.Count, Is.EqualTo(0));
            Assert.That(original.CapacityLeft, Is.EqualTo(size - SlottedArray.HeaderSize));
        }

        original.GetShouldFail(key1);
        original.GetShouldFail(key2);
        original.GetShouldFail(key3);
        original.GetShouldFail(key4);
        original.GetShouldFail(key5);

        // copy should have all but empty
        Assert.That(copy0.Count, Is.EqualTo(1));
        copy0.GetShouldFail(key1);
        copy0.GetShouldFail(key2);
        copy0.GetShouldFail(key3);
        copy0.GetShouldFail(key4);
        copy0.GetAssert(key5, Data(5));
    }

    [Test]
    public void Move_to_2()
    {
        SlottedArray original = new(stackalloc byte[256]);
        SlottedArray copy0 = new(stackalloc byte[256]);
        SlottedArray copy1 = new(stackalloc byte[256]);

        NibblePath key0 = NibblePath.Empty;
        NibblePath key1 = NibblePath.Parse("1");
        NibblePath key2 = NibblePath.Parse("23");
        NibblePath key3 = NibblePath.Parse("345");
        NibblePath key4 = NibblePath.Parse("4567");
        NibblePath key5 = NibblePath.Parse("56789");

        original.SetAssert(key0, Data(0));
        original.SetAssert(key1, Data(1));
        original.SetAssert(key2, Data(2));
        original.SetAssert(key3, Data(3));
        original.SetAssert(key4, Data(4));
        original.SetAssert(key5, Data(5));

        original.MoveNonEmptyKeysTo(new MapSource(copy0, copy1));

        // original should have only empty
        Assert.That(original.Count, Is.EqualTo(1));
        original.GetAssert(key0, Data(0));
        original.GetShouldFail(key1);
        original.GetShouldFail(key2);
        original.GetShouldFail(key3);
        original.GetShouldFail(key4);
        original.GetShouldFail(key5);

        // copy0 should have key2 and key4 and nothing else
        Assert.That(copy0.Count, Is.EqualTo(2));
        copy0.GetShouldFail(key0);
        copy0.GetShouldFail(key1);
        copy0.GetAssert(key2, Data(2));
        copy0.GetShouldFail(key3);
        copy0.GetAssert(key4, Data(4));
        copy0.GetShouldFail(key5);

        // copy1 should have key1 and key3 and key5 and nothing else
        Assert.That(copy1.Count, Is.EqualTo(3));
        copy1.GetShouldFail(key0);
        copy1.GetAssert(key1, Data(1));
        copy1.GetShouldFail(key2);
        copy1.GetAssert(key3, Data(3));
        copy1.GetShouldFail(key4);
        copy1.GetAssert(key5, Data(5));
    }

    [Test]
    public void Move_to_4()
    {
        SlottedArray original = new(stackalloc byte[256]);
        SlottedArray copy0 = new(stackalloc byte[256]);
        SlottedArray copy1 = new(stackalloc byte[256]);
        SlottedArray copy2 = new(stackalloc byte[256]);
        SlottedArray copy3 = new(stackalloc byte[256]);

        NibblePath key0 = NibblePath.Empty;
        NibblePath key1 = NibblePath.Parse("1");
        NibblePath key2 = NibblePath.Parse("23");
        NibblePath key3 = NibblePath.Parse("345");
        NibblePath key4 = NibblePath.Parse("4567");
        NibblePath key5 = NibblePath.Parse("56789");

        original.SetAssert(key0, Data(0));
        original.SetAssert(key1, Data(1));
        original.SetAssert(key2, Data(2));
        original.SetAssert(key3, Data(3));
        original.SetAssert(key4, Data(4));
        original.SetAssert(key5, Data(5));

        original.MoveNonEmptyKeysTo(new MapSource(copy0, copy1, copy2, copy3));

        // original should have only empty
        Assert.That(original.Count, Is.EqualTo(1));
        original.GetAssert(key0, Data(0));
        original.GetShouldFail(key1);
        original.GetShouldFail(key2);
        original.GetShouldFail(key3);
        original.GetShouldFail(key4);
        original.GetShouldFail(key5);

        // copy0 should have key4
        Assert.That(copy0.Count, Is.EqualTo(1));
        copy0.GetShouldFail(key0);
        copy0.GetShouldFail(key1);
        copy0.GetShouldFail(key2);
        copy0.GetShouldFail(key3);
        copy0.GetAssert(key4, Data(4));
        copy0.GetShouldFail(key5);

        // copy1 should have key1 and key5 and nothing else
        Assert.That(copy1.Count, Is.EqualTo(2));
        copy1.GetShouldFail(key0);
        copy1.GetAssert(key1, Data(1));
        copy1.GetShouldFail(key2);
        copy1.GetShouldFail(key3);
        copy1.GetShouldFail(key4);
        copy1.GetAssert(key5, Data(5));

        // copy1 should have key2 and nothing else
        Assert.That(copy2.Count, Is.EqualTo(1));
        copy2.GetShouldFail(key0);
        copy2.GetShouldFail(key1);
        copy2.GetAssert(key2, Data(2));
        copy2.GetShouldFail(key3);
        copy2.GetShouldFail(key4);
        copy2.GetShouldFail(key5);

        // copy1 should have key2 and nothing else
        Assert.That(copy3.Count, Is.EqualTo(1));
        copy3.GetShouldFail(key0);
        copy3.GetShouldFail(key1);
        copy3.GetShouldFail(key2);
        copy3.GetAssert(key3, Data(3));
        copy3.GetShouldFail(key4);
        copy3.GetShouldFail(key5);
    }

    [Test]
    public void Move_to_8()
    {
        SlottedArray original = new(stackalloc byte[512]);
        SlottedArray copy0 = new(stackalloc byte[128]);
        SlottedArray copy1 = new(stackalloc byte[128]);
        SlottedArray copy2 = new(stackalloc byte[128]);
        SlottedArray copy3 = new(stackalloc byte[128]);
        SlottedArray copy4 = new(stackalloc byte[128]);
        SlottedArray copy5 = new(stackalloc byte[128]);
        SlottedArray copy6 = new(stackalloc byte[128]);
        SlottedArray copy7 = new(stackalloc byte[128]);

        NibblePath key0 = NibblePath.Empty;
        NibblePath key1 = NibblePath.Parse("1");
        NibblePath key2 = NibblePath.Parse("23");
        NibblePath key3 = NibblePath.Parse("345");
        NibblePath key4 = NibblePath.Parse("4567");
        NibblePath key5 = NibblePath.Parse("56789");
        NibblePath key6 = NibblePath.Parse("6789AB");
        NibblePath key7 = NibblePath.Parse("789ABCD");
        NibblePath key8 = NibblePath.Parse("89ABCDEF");

        original.SetAssert(key0, Data(0));
        original.SetAssert(key1, Data(1));
        original.SetAssert(key2, Data(2));
        original.SetAssert(key3, Data(3));
        original.SetAssert(key4, Data(4));
        original.SetAssert(key5, Data(5));
        original.SetAssert(key6, Data(6));
        original.SetAssert(key7, Data(7));
        original.SetAssert(key8, Data(8));

        original.MoveNonEmptyKeysTo(new MapSource(copy0, copy1, copy2, copy3, copy4, copy5, copy6, copy7));

        // original should have only empty
        HasOnly(original, 0);

        HasOnly(copy0, 8);
        HasOnly(copy1, 1);
        HasOnly(copy2, 2);
        HasOnly(copy3, 3);
        HasOnly(copy4, 4);
        HasOnly(copy5, 5);
        HasOnly(copy6, 6);
        HasOnly(copy7, 7);

        return;

        static void HasOnly(in SlottedArray map, int key)
        {
            Assert.That(map.Count, Is.EqualTo(1));

            for (byte i = 0; i < 8; i++)
            {
                NibblePath path = i switch
                {
                    0 => NibblePath.Empty,
                    1 => NibblePath.Parse("1"),
                    2 => NibblePath.Parse("23"),
                    3 => NibblePath.Parse("345"),
                    4 => NibblePath.Parse("4567"),
                    5 => NibblePath.Parse("56789"),
                    6 => NibblePath.Parse("6789AB"),
                    7 => NibblePath.Parse("789ABCD"),
                    _ => NibblePath.Parse("89ABCDEF")
                };

                if (i == key)
                {
                    map.GetAssert(path, Data(i));
                }
                else
                {
                    map.GetShouldFail(path);
                }
            }
        }
    }

    [Test]
    public void Hashing()
    {
        Dictionary<ushort, string> hashes = [];

        // empty
        Unique("");

        // single nibble
        Unique("A");
        Unique("B");
        Unique("C");
        Unique("7");

        // two nibbles
        Unique("AC");
        Unique("AB");
        Unique("BC");

        // three nibbles
        Unique("ADC");
        Unique("AEB");
        Unique("BEC");

        // four nibbles
        Unique("ADC1");
        Unique("AEB1");
        Unique("BEC1");

        // 5 nibbles, with last changed
        Unique("AD0C2");
        Unique("AE0B2");
        Unique("BE0C2");

        // 6 nibbles, with last changed
        Unique("AD00C4");
        Unique("AE00B4");
        Unique("BE00C4");

        return;

        void Unique(string key)
        {
            NibblePath path = NibblePath.Parse(key);
            ushort hash = SlottedArray.HashForTests(path);

            if (!hashes.TryAdd(hash, key))
            {
                Assert.Fail($"The hash for {key} is the same as for {hashes[hash]}");
            }
        }
    }

    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(1, 1)]
    [TestCase(0, 2)]
    [TestCase(1, 2)]
    [TestCase(0, 3)]
    [TestCase(1, 3)]
    [TestCase(0, 4)]
    [TestCase(1, 4)]
    [TestCase(0, 6)]
    [TestCase(1, 6)]
    [TestCase(0, 64)]
    [TestCase(1, 63)]
    [TestCase(1, 62)]
    public void Prepare_UnPrepare(int sliceFrom, int length)
    {
        NibblePath key = NibblePath.FromKey(Keccak.EmptyTreeHash).Slice(sliceFrom, length);

        // prepare
        ushort hash = SlottedArray.PrepareKeyForTests(key, out byte preamble, out NibblePath trimmed);
        ReadOnlySpan<byte> written = trimmed.IsEmpty ? ReadOnlySpan<byte>.Empty : trimmed.WriteTo(stackalloc byte[33]);

        Span<byte> working = stackalloc byte[32];

        NibblePath unprepared = SlottedArray.UnPrepareKeyForTests(hash, preamble, written, working, out ReadOnlySpan<byte> data);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(data.IsEmpty, Is.True);
            Assert.That(key.Equals(unprepared), Is.True);
        }
    }

    [Test]
    public void Dump_and_load()
    {
        SlottedArray map = new(stackalloc byte[512]);

        NibblePath key0 = NibblePath.Empty;
        NibblePath key1 = NibblePath.Parse("1");
        NibblePath key2 = NibblePath.Parse("23");
        NibblePath key3 = NibblePath.Parse("345");
        NibblePath key4 = NibblePath.Parse("4567");
        NibblePath key5 = NibblePath.Parse("56789");
        NibblePath key6 = NibblePath.Parse("6789AB");
        NibblePath key7 = NibblePath.Parse("789ABCD");
        NibblePath key8 = NibblePath.Parse("89ABCDEF");

        map.SetAssert(key0, Data(0));
        map.SetAssert(key1, Data(1));
        map.SetAssert(key2, Data(2));
        map.SetAssert(key3, Data(3));
        map.SetAssert(key4, Data(4));
        map.SetAssert(key5, Data(5));
        map.SetAssert(key6, Data(6));
        map.SetAssert(key7, Data(7));
        map.SetAssert(key8, Data(8));

        string dump = map.DumpToHexString();
        SlottedArray loaded = SlottedArray.LoadFromDump(dump);

        loaded.GetAssert(key0, Data(0));
        loaded.GetAssert(key1, Data(1));
        loaded.GetAssert(key2, Data(2));
        loaded.GetAssert(key3, Data(3));
        loaded.GetAssert(key4, Data(4));
        loaded.GetAssert(key5, Data(5));
        loaded.GetAssert(key6, Data(6));
        loaded.GetAssert(key7, Data(7));
        loaded.GetAssert(key8, Data(8));
    }

    private static void AssertItem(SlottedArray.Item item, in NibblePath key, ReadOnlySpan<byte> data)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.Key.Equals(key), Is.True);
            Assert.That(item.RawData.ToArray(), Is.EqualTo(data.ToArray()));
        }
    }
}

file static class FixedMapTestExtensions
{
    public static void SetAssert(this SlottedArray map, in NibblePath key, ReadOnlySpan<byte> data,
        string? because = null) =>
        Assert.That(map.TrySet(key, data), Is.True, because ?? "TrySet should succeed");

    public static void SetAssert(this SlottedArray map, in ReadOnlySpan<byte> key, ReadOnlySpan<byte> data,
        string? because = null) =>
        map.SetAssert(NibblePath.FromKey(key), data, because);

    public static void DeleteAssert(this SlottedArray map, in ReadOnlySpan<byte> key) =>
        Assert.That(map.Delete(NibblePath.FromKey(key)), Is.True, "Delete should succeed");

    public static void GetAssert(this SlottedArray map, in ReadOnlySpan<byte> key, ReadOnlySpan<byte> expected) =>
        map.GetAssert(NibblePath.FromKey(key), expected);

    public static void GetAssert(this SlottedArray map, in NibblePath key, ReadOnlySpan<byte> expected)
    {
        if (!map.TryGet(key, out ReadOnlySpan<byte> actual))
        {
            Assert.Fail("The key should exist");
        }

        Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()), "Actual data should equal expected");
    }

    public static void GetShouldFail(this SlottedArray map, in ReadOnlySpan<byte> key) =>
        map.GetShouldFail(NibblePath.FromKey(key));

    public static void GetShouldFail(this SlottedArray map, in NibblePath key) =>
        Assert.That(map.TryGet(key, out _), Is.False, "The key should not exist");
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Era1;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework.Constraints;
using Snappier;
namespace Nethermind.Era.Test;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task Test1()
    {
        var ms = new MemoryStream();    
        var sut = await E2Store.ForWrite(ms);

        await sut.WriteEntry(EntryTypes.Version, Array.Empty<byte>());
    }

    [Test]
    public async Task TestHistoryImport()
    {
        var eraFiles = E2Store.GetAllEraFiles("data", "mainnet");

        Directory.CreateDirectory("temp");

        foreach (var era in eraFiles)
        {
            var sut = await EraIterator.Create(era);

            string tempEra = Path.Combine("temp", Path.GetFileName(era));
            var builder = new EraBuilder(tempEra);

            await foreach ((Block b, TxReceipt[] r, UInt256 td) in sut)
            {
                Debug.WriteLine($"Reencoding block");

                Rlp encodedHeader = new HeaderDecoder().Encode(b.Header);
                Rlp encodedBody = new BlockBodyDecoder().Encode(b.Body);

                await builder.Add(b, r, td);
            }
            await builder.Finalize();
            builder.Dispose();

            var sut2 = await EraIterator.Create(tempEra);
            await foreach ((Block b, TxReceipt[] r, UInt256 td) in sut2)
            {
                Rlp encodedHeader = new HeaderDecoder().Encode(b.Header);
                Rlp encodedBody = new BlockBodyDecoder().Encode(b.Body);
            }

            Assert.True(File.ReadAllBytes(era).SequenceEqual(File.ReadAllBytes(tempEra)));

        }

        Directory.Delete("temp", true);
    }

    [Test]  
    public async Task TestE2Store()
    {
        var sut = await E2Store.ForRead(File.OpenRead("data/mainnet-00000-096013b1.era1"));

        var testData = Convert.FromHexString("FF060000734E6150705900B00000FCED3AFBFB0310F901F8A0007A010088A01DCC4DE8DEC75D7AAB85B567B6CCD41AD312451B948A7413F0A142FD40D4934794004A4200F043A09F88BE00EEE1114EDFD9372F52560AAB3980A142EFE8B5B39A09644075084275A056E81F171BCC55A6FF8345E692C0F86E5B48E01B996CADC001622FB5E363B421A0567A210004B9014A7800FE0100FE0100FE0100B601002C83020000808347E7C480808082C90134880000000000000000843B9ACA00");

        var compressedHeaders = await sut.FindAll(EntryTypes.CompressedHeader);
        
        foreach (var cb in compressedHeaders)
        {
            try
            {
                var output = new byte[1000];
                var x = await sut.ReadEntryValueAsSnappy(output, cb);

                void Test()
                {
                    var s = new Span<byte>(output, 0, x);
                    var decoded = Rlp.Decode<BlockHeader>(s);
                }
                Test();

            }
            catch (InvalidDataException)
            {
                continue;
            }
            
        }
    }

}

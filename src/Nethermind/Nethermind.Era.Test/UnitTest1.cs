// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Era1;
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
        var sut = await E2Store.FromStream(ms);

        sut.WriteEntry(E2Store.TypeVersion, Array.Empty<byte>());
    }

    [Test]
    public async Task TestHistoryImport()
    {
        var eraFiles = E2Store.GetAllEraFiles("data", "mainnet");

        foreach (var era in eraFiles)
        {
            var sut = await EraIterator.Create(era);

            await foreach ((Block b, TxReceipt[] r) in sut)
            {
                Debug.WriteLine($"Reencoding block");

                Rlp encodedHeader = new HeaderDecoder().Encode(b.Header);
                Debug.WriteLine($"Encoded header {BitConverter.ToString(encodedHeader.Bytes).Replace("-","")}");
                Rlp encodedBody = new BlockBodyDecoder().Encode(b.Body);
                Debug.WriteLine($"Encoded body {BitConverter.ToString(encodedBody.Bytes).Replace("-", "")}");

                NettyRlpStream encodedReceipt = new ReceiptDecoder().EncodeToNewNettyStream(r);
            }
        }
        
    }

    [Test]  
    public async Task TestE2Store()
    {
        var sut = await E2Store.FromStream(File.OpenRead("data/mainnet-00000-096013b1.era1"));

        var testData = Convert.FromHexString("FF060000734E6150705900B00000FCED3AFBFB0310F901F8A0007A010088A01DCC4DE8DEC75D7AAB85B567B6CCD41AD312451B948A7413F0A142FD40D4934794004A4200F043A09F88BE00EEE1114EDFD9372F52560AAB3980A142EFE8B5B39A09644075084275A056E81F171BCC55A6FF8345E692C0F86E5B48E01B996CADC001622FB5E363B421A0567A210004B9014A7800FE0100FE0100FE0100B601002C83020000808347E7C480808082C90134880000000000000000843B9ACA00");

        var compressedHeaders = await sut.FindAll(E2Store.TypeCompressedHeader);
        
        foreach (var cb in compressedHeaders)
        {
            try
            {
                void Test()
                {
                    var output = new byte[1000];
                    var decompressionStream = new SnappyStream(cb.ValueAsStream(), System.IO.Compression.CompressionMode.Decompress);
                    var x = decompressionStream.Read(output, 0, output.Length);
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

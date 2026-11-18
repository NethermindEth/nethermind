// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using CryptSharp.Utility;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test;

[Parallelizable(ParallelScope.All)]
public class KeyStoreTests
{
    private static IEnumerable<TestCaseData> IoFailureCases()
    {
        foreach (bool maskSensitiveData in new[] { false, true })
        {
            foreach (string operation in new[] { nameof(FileKeyStore.GetKeyData), nameof(FileKeyStore.GetKeyAddresses), nameof(FileKeyStore.StoreKey), nameof(FileKeyStore.DeleteKey) })
                yield return new TestCaseData(operation, maskSensitiveData, false);
            yield return new TestCaseData(nameof(FileKeyStore.GetKeyData), maskSensitiveData, true);
        }
    }

    [TestCaseSource(nameof(IoFailureCases))]
    [NonParallelizable]
    public void Io_failure_diagnostics_follow_sensitive_masking(string operation, bool maskSensitiveData, bool lockedFile)
    {
        using TempPath directory = TempPath.GetTempDirectory($"private-keystore-{Guid.NewGuid():N}");
        string keyPath = Path.Combine(directory.Path, $"UTC--test--{TestItem.AddressA.ToString(false, false)}");
        if (lockedFile)
        {
            Directory.CreateDirectory(directory.Path);
            File.WriteAllText(keyPath, "key file contents");
        }
        using FileStream keyLock = lockedFile ? File.Open(keyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        IKeyStoreIOSettingsProvider settings = Substitute.For<IKeyStoreIOSettingsProvider>();
        if (lockedFile) settings.StoreDirectory.Returns(directory.Path);
        else settings.StoreDirectory.Returns(_ => throw new UnauthorizedAccessException($"Access to {directory.Path} is denied."));
        TestErrorLogManager logs = new();
        using ICryptoRandom random = Substitute.For<ICryptoRandom>();
        FileKeyStore store = new(new KeyStoreConfig(), new EthereumJsonSerializer(), Substitute.For<ISymmetricEncrypter>(), random, logs, settings);
        bool previous = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = maskSensitiveData;
        try
        {
            Result result = operation switch
            {
                nameof(FileKeyStore.GetKeyData) => store.GetKeyData(TestItem.AddressA).Result,
                nameof(FileKeyStore.GetKeyAddresses) => store.GetKeyAddresses().Result,
                nameof(FileKeyStore.StoreKey) => store.StoreKey(TestItem.AddressA, new KeyStoreItem()),
                _ => store.DeleteKey(TestItem.AddressA)
            };

            TestErrorLogManager.Error error = logs.Errors.Single();
            string message = operation switch
            {
                nameof(FileKeyStore.GetKeyData) => $"Error during reading key for address: {TestItem.AddressA}",
                nameof(FileKeyStore.GetKeyAddresses) => "Error during getting addresses",
                nameof(FileKeyStore.StoreKey) => $"Error during persisting key for address: {TestItem.AddressA}",
                _ => $"Error during deleting key for address: {TestItem.AddressA}"
            };
            string exceptionType = lockedFile ? nameof(IOException) : nameof(UnauthorizedAccessException);
            string rendered = $"{error.Text}{Environment.NewLine}{error.Exception}";
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.ResultType, Is.EqualTo(ResultType.Failure));
                Assert.That(result.Error, Is.EqualTo(operation == nameof(FileKeyStore.GetKeyData) ? "Cannot find key" : message));
                Assert.That(error.Text, Is.EqualTo(maskSensitiveData ? $"{message}: {exceptionType}" : message));
                Assert.That(error.Exception is null, Is.EqualTo(maskSensitiveData));
                Assert.That(rendered.Contains(directory.Path), Is.EqualTo(!maskSensitiveData));
            }
        }
        finally
        {
            SensitiveLogMasking.Enabled = previous;
        }
    }

    private class TestContext
    {
        public FileKeyStore Store { get; }
        public IJsonSerializer Serializer { get; }
        public IKeyStoreConfig KeyStoreConfig { get; }
        public ICryptoRandom CryptoRandom { get; }
        public SecureString TestPasswordSecured { get; }
        public SecureString WrongPasswordSecured { get; }
        public const string TestPassword = "testpassword";

        public TestContext()
        {
            KeyStoreConfig = new KeyStoreConfig();
            KeyStoreConfig.KeyStoreDirectory = NUnit.Framework.TestContext.CurrentContext.WorkDirectory;

            TestPasswordSecured = new SecureString();
            WrongPasswordSecured = new SecureString();

            for (int i = 0; i < TestPassword.Length; i++)
            {
                TestPasswordSecured.AppendChar(TestPassword[i]);
                WrongPasswordSecured.AppendChar('*');
            }

            TestPasswordSecured.MakeReadOnly();
            WrongPasswordSecured.MakeReadOnly();

            ILogManager logger = LimboLogs.Instance;
            Serializer = new EthereumJsonSerializer();
            CryptoRandom = new CryptoRandom();
            Store = new FileKeyStore(KeyStoreConfig, Serializer, new AesEncrypter(KeyStoreConfig, logger), CryptoRandom, logger, new PrivateKeyStoreIOSettingsProvider(KeyStoreConfig));
        }
    }

    [Test]
    public void Verify_rejects_null_json()
    {
        TestContext test = new();

        (KeyStoreItem keyData, Result result) = test.Store.Verify("null");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(keyData, Is.Null);
        }
    }

    [Test]
    public void GetKeyData_returns_failure_for_missing_key()
    {
        TestContext test = new();

        (KeyStoreItem keyData, Result result) = test.Store.GetKeyData(Address.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(keyData, Is.Null);
        }
    }

    [Test]
    public void GetKeyAddresses_returns_empty_collection_on_failure()
    {
        TestContext test = new();
        test.KeyStoreConfig.KeyStoreDirectory = "\0";

        (IReadOnlyCollection<Address> addresses, Result result) = test.Store.GetKeyAddresses();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(addresses, Is.Empty);
        }
    }

    [TestCase("{\"address\":\"20b2e4bb8688a44729780d15dc64adb42f9f5a0a\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"d30cbb0f5b30ef86e57b7fa111307398b911b8c0a3eab4ac4edc4b2c8839afbe\",\"cipherparams\":{\"iv\":\"1e29e79023d73be3f3bb065ca9ddc078\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"fffcd979c3223b3cdfcb2cf21b07bd4313e6f8d02af8a79a5c5dc879a25680d3\"},\"mac\":\"3ac5a539775c33bd73adfd2c0d4ef8c9154e4b404e2a15c77b0e6c78cb90df20\"},\"id\":\"68462de1-4114-4f92-828b-883fae5f779c\",\"version\":3}")]
    [TestCase("{\"address\":\"746526c3a59db995b914a319306cd7ae35dc50c5\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"644b1af45188b23f6195abd2b0563d7b079ff6622e5ac61767cd81cbd621a13e\",\"cipherparams\":{\"iv\":\"844c895835de8571409b8a76a75672b2\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"e7df76e322e444ed61314fa5261cf0ac02b9c057fe626a74a37c5255c16a8d61\"},\"mac\":\"48f26081eec397b818ed4e2cb3b1c04908c671a81d6b183e9965d869bd001862\"},\"id\":\"6ee56be1-367f-4b41-a25d-f60e0a7bfe42\",\"version\":3}")]
    [TestCase("{\"address\":\"aa42104423e00a862b616f2f712a1b17d308bbc9\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"450c341ab64c39237039a30a8d84cc112dfbdda889caa19201b0cf8473680936\",\"cipherparams\":{\"iv\":\"923d950dcdba710a0c8e240441e0a227\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"101185ea81a1067591bce5323d75648b753f71becc22a4ebd55256593a705698\"},\"mac\":\"dc0a3bc555ac8f22d84115968b5fde6f50eb065ff7fe47a1da30de668a5ca864\"},\"id\":\"339ef573-a7d5-4bd0-86b2-3b1e420439d7\",\"version\":3}")]
    [TestCase("{\"address\":\"25dead29c683c5db3e0fabcf8f3757cdb0abe549\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"4fd59f3a3fa1bed32774b29a40886d5489c0c06a8da014cb44b25792f6c32cb2\",\"cipherparams\":{\"iv\":\"6b850162043a0a879726839cfca55220\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"cafbe520e0d711cf32d9a2e6b2ecbd231cc7aed09018c5032c637436e02754d1\"},\"mac\":\"379f51c673f1f355a6ffc92b31b37381670eea2e0e23604a2572f5df650d148e\"},\"id\":\"fc7ff6bf-c51e-4e02-bb7c-0c91a3eeab4c\",\"version\":3}")]
    public void Can_unlock_test_accounts(string keyJson)
    {
        TestContext test = new();
        EthereumJsonSerializer serializer = new();
        KeyStoreItem item = serializer.Deserialize<KeyStoreItem>(keyJson);

        SecureString securePassword = new();
        string password = "testpuppeth";
        for (int i = 0; i < password.Length; i++)
        {
            securePassword.AppendChar(password[i]);
        }

        securePassword.MakeReadOnly();

        test.Store.StoreKey(new Address(item.Address), item);
        try
        {
            (PrivateKey key, Result result) = test.Store.GetKey(new Address(item.Address), securePassword);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), item.Address + " " + result.Error);
                Assert.That(item.Address, Is.EqualTo(key.Address.ToString(false, false)));
            }
        }
        finally
        {
            test.Store.DeleteKey(new Address(item.Address));
        }
    }

    [TestCase("{\"address\":\"20b2e4bb8688a44729780d15dc64adb42f9f5a0a\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"d30cbb0f5b30ef86e57b7fa111307398b911b8c0a3eab4ac4edc4b2c8839afbe\",\"cipherparams\":{\"iv\":\"1e29e79023d73be3f3bb065ca9ddc078\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"fffcd979c3223b3cdfcb2cf21b07bd4313e6f8d02af8a79a5c5dc879a25680d3\"},\"mac\":\"3ac5a539775c33bd73adfd2c0d4ef8c9154e4b404e2a15c77b0e6c78cb90df20\"},\"id\":\"68462de1-4114-4f92-828b-883fae5f779c\",\"version\":3}", Ignore = "Order of fields changed from geth to mycryptowallet.")]
    [TestCase("{\"address\":\"746526c3a59db995b914a319306cd7ae35dc50c5\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"644b1af45188b23f6195abd2b0563d7b079ff6622e5ac61767cd81cbd621a13e\",\"cipherparams\":{\"iv\":\"844c895835de8571409b8a76a75672b2\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"e7df76e322e444ed61314fa5261cf0ac02b9c057fe626a74a37c5255c16a8d61\"},\"mac\":\"48f26081eec397b818ed4e2cb3b1c04908c671a81d6b183e9965d869bd001862\"},\"id\":\"6ee56be1-367f-4b41-a25d-f60e0a7bfe42\",\"version\":3}", Ignore = "Order of fields changed from geth to mycryptowallet.")]
    [TestCase("{\"address\":\"aa42104423e00a862b616f2f712a1b17d308bbc9\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"450c341ab64c39237039a30a8d84cc112dfbdda889caa19201b0cf8473680936\",\"cipherparams\":{\"iv\":\"923d950dcdba710a0c8e240441e0a227\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"101185ea81a1067591bce5323d75648b753f71becc22a4ebd55256593a705698\"},\"mac\":\"dc0a3bc555ac8f22d84115968b5fde6f50eb065ff7fe47a1da30de668a5ca864\"},\"id\":\"339ef573-a7d5-4bd0-86b2-3b1e420439d7\",\"version\":3}", Ignore = "Order of fields changed from geth to mycryptowallet.")]
    [TestCase("{\"address\":\"25dead29c683c5db3e0fabcf8f3757cdb0abe549\",\"crypto\":{\"cipher\":\"aes-128-ctr\",\"ciphertext\":\"4fd59f3a3fa1bed32774b29a40886d5489c0c06a8da014cb44b25792f6c32cb2\",\"cipherparams\":{\"iv\":\"6b850162043a0a879726839cfca55220\"},\"kdf\":\"scrypt\",\"kdfparams\":{\"dklen\":32,\"n\":262144,\"p\":1,\"r\":8,\"salt\":\"cafbe520e0d711cf32d9a2e6b2ecbd231cc7aed09018c5032c637436e02754d1\"},\"mac\":\"379f51c673f1f355a6ffc92b31b37381670eea2e0e23604a2572f5df650d148e\"},\"id\":\"fc7ff6bf-c51e-4e02-bb7c-0c91a3eeab4c\",\"version\":3}", Ignore = "Order of fields changed from geth to mycryptowallet.")]
    public void Same_storage_format_as_in_geth(string keyJson)
    {
        TestContext test = new();
        EthereumJsonSerializer serializer = new();
        KeyStoreItem item = serializer.Deserialize<KeyStoreItem>(keyJson);

        SecureString securePassword = new();
        string password = "testpuppeth";
        for (int i = 0; i < password.Length; i++)
        {
            securePassword.AppendChar(password[i]);
        }

        Address address = new(item.Address);
        test.Store.StoreKey(address, item);

        try
        {
            string[] files = test.Store.FindKeyFiles(address);
            Assert.That(files.Length, Is.EqualTo(1));
            string text = File.ReadAllText(files[0]);
            Assert.That(text, Is.EqualTo(keyJson), "same json");
        }
        finally
        {
            test.Store.DeleteKey(new Address(item.Address));
        }
    }

    [Test]
    public void GenerateKeyAddressesTest()
    {
        TestContext test = new();

        PrivateKey key1;
        PrivateKey key2;

        string notAKeyPath = Path.Combine(test.KeyStoreConfig.KeyStoreDirectory, "not_a_key");

        using (FileStream stream = File.Create(notAKeyPath))
        {
        }

        try
        {
            Result result;
            (key1, result) = test.Store.GenerateKey(test.TestPasswordSecured);
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "generate key 1");

            (key2, result) = test.Store.GenerateKey(test.TestPasswordSecured);
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "generate key 2");

            (IReadOnlyCollection<Address> addresses, Result getAllResult) = test.Store.GetKeyAddresses();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(getAllResult.ResultType, Is.EqualTo(ResultType.Success), "get key");
                Assert.That(addresses.Count >= 2, Is.True);
                Assert.That(addresses.FirstOrDefault(x => x.Equals(key1.Address)), Is.Not.Null, "key 1 not null");
                Assert.That(addresses.FirstOrDefault(x => x.Equals(key2.Address)), Is.Not.Null, "key 2 not null");
            }

            result = test.Store.DeleteKey(key1.Address);
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "delete key 1");

            result = test.Store.DeleteKey(key2.Address);
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "delete key 2");
        }
        finally
        {
            File.Delete(notAKeyPath);
        }
    }

    [Test]
    public void GenerateKeyTest()
    {
        TestContext test = new();
        (PrivateKey, Result) key = test.Store.GenerateKey(test.TestPasswordSecured);
        Assert.That(key.Item2.ResultType, Is.EqualTo(ResultType.Success));

        (PrivateKey, Result) persistedKey = test.Store.GetKey(key.Item1.Address, test.TestPasswordSecured);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedKey.Item2.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(Bytes.AreEqual(key.Item1.KeyBytes, persistedKey.Item1.KeyBytes), Is.True);
        }

        Result result = test.Store.DeleteKey(key.Item1.Address);
        Assert.That(result.ResultType, Is.EqualTo(ResultType.Success));

        (PrivateKey, Result) deletedKey = test.Store.GetKey(key.Item1.Address, test.TestPasswordSecured);
        Assert.That(deletedKey.Item2.ResultType, Is.EqualTo(ResultType.Failure));
    }


    [Test]
    public void KeyStoreVersionMismatchTest()
    {
        TestContext test = new();
        //generate key
        (PrivateKey key, Result storeResult) = test.Store.GenerateKey(test.TestPasswordSecured);
        Assert.That(storeResult.ResultType, Is.EqualTo(ResultType.Success), "generate result");

        (KeyStoreItem keyData, Result result) = test.Store.GetKeyData(key.Address);
        Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), "load result");

        Result deleteResult = test.Store.DeleteKey(key.Address);
        Assert.That(deleteResult.ResultType, Is.EqualTo(ResultType.Success), "delete result");

        keyData.Version = 0;
        test.Store.StoreKey(key.Address, keyData);

        (_, Result loadResult) = test.Store.GetKey(key.Address, test.TestPasswordSecured);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadResult.ResultType, Is.EqualTo(ResultType.Failure), "bad load result");
            Assert.That(loadResult.Error, Is.EqualTo("KeyStore version mismatch"));
        }

        test.Store.DeleteKey(key.Address);
    }

    [Test]
    public void WrongPasswordTest()
    {
        TestContext test = new();
        (PrivateKey key, Result generateResult) = test.Store.GenerateKey(test.TestPasswordSecured);
        Assert.That(generateResult.ResultType, Is.EqualTo(ResultType.Success));

        (PrivateKey keyRestored, Result getResult) = test.Store.GetKey(key.Address, test.TestPasswordSecured);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(getResult.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(Bytes.AreEqual(key.KeyBytes, keyRestored.KeyBytes), Is.True);
        }

        (PrivateKey _, Result wrongResult) = test.Store.GetKey(key.Address, test.WrongPasswordSecured);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrongResult.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(wrongResult.Error, Is.EqualTo("Incorrect MAC"));
        }

        Result deleteResult = test.Store.DeleteKey(key.Address);
        Assert.That(deleteResult.ResultType, Is.EqualTo(ResultType.Success));
    }

    [Test]
    public void ShouldSaveFileWithoutBom()
    {
        TestContext test = new();
        const string bomBytesHex = "efbbbf";
        const string validBytesHex = "7b2276";
        (PrivateKey key, Result _) = test.Store.GenerateKey(test.TestPasswordSecured);
        string directory = test.KeyStoreConfig.KeyStoreDirectory.GetApplicationResourcePath();
        string addressHex = key.Address.ToString(false, false);
        string file = Directory.GetFiles(directory).SingleOrDefault(f => f.Contains(addressHex));
        byte[] bytes = File.ReadAllBytes(file);
        test.Store.DeleteKey(key.Address);
        string bytesHex = bytes.ToHexString();
        Assert.That(bytesHex, Does.Not.StartWith(bomBytesHex));
        Assert.That(bytesHex, Does.StartWith(validBytesHex));
    }

    // Password, key, salt and IV of the test vectors of the Web3 Secret Storage definition,
    // https://ethereum.org/developers/docs/data-structures-and-encoding/web3-secret-storage/
    private const string VectorPassword = "testpassword";
    private const string VectorKey = "7a28b5ba57c53603b0b07b56bba752f7784bf506fa95edc395f5cf6c7514fe9d";
    private const string VectorSalt = "25710c2ccd7c610b24d068af83b959b7a0e5f40641f0c82daeb1345766191034";
    private const string VectorIv = "740770fce12ce862af21264dab25f1da";
    private const string Pbkdf2Vector = """{"crypto":{"cipher":"aes-128-ctr","cipherparams":{"iv":"6087dab2f9fdbbfaddc31a909735c1e6"},"ciphertext":"5318b4d5bcd28de64ee5559e671353e16f075ecae9f99c7a79a38af5f869aa46","kdf":"pbkdf2","kdfparams":{"c":262144,"dklen":32,"prf":"hmac-sha256","salt":"ae3cd4e7013836a3df6bd7241b12db061dbe2c6785853cce422d148a624ce0bd"},"mac":"517ead924a9d0dc3124507e3393d175ce3ff7c1e96529c6c555ce9e51205e9b2"},"id":"3198bc9c-6672-5ab3-d995-4942343ae5b6","version":3}""";

    // Key files for the vector key, salt and IV with n = 1024, as written with lower-case names.
    private const string CtrCipherText = "07d6803a09be626843cf064d8e8f986eff6e65f99154e356691f912458153078";
    private const string CtrMac = "35cd59790186d17560c9c07127567ddb55286c5a5112b7f7f85e09d51610a294";
    private const string CbcCipherText = "172e3552758013b6b36d714c3b150e3dcab629bde95d3d6f4180f47f657b52852996681653e4ac003f99ee5e9737b1cf";
    private const string CbcMac = "6adf13188819da274d567b967ddab1885a5b43789bec718d1bee45e3e9b1fc87";

    // Pins the whole file; its ciphertext and MAC match an independent computation of the definition's algorithm.
    // The definition's own scrypt vector cannot serve here: its derived key does not follow from its salt and parameters.
    [Test]
    public void Default_settings_write_the_same_key_file()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        ICryptoRandom random = FixedRandom();
        FileKeyStore store = CreateStore(new KeyStoreConfig { KeyStoreDirectory = directory.Path }, random);
        PrivateKey key = new(VectorKey);

        Result result = store.StoreKey(key, VectorPassword.Secure());

        Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), result.Error);
        string file = Encoding.UTF8.GetString(File.ReadAllBytes(store.FindKeyFiles(key.Address).Single()));
        string id = store.GetKeyData(key.Address).KeyData.Id;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(random.ReceivedCalls().Select(static call => call.GetArguments()[0]), Is.EqualTo(new[] { 32, 16 }), "salt, then IV");
            Assert.That(file, Is.EqualTo($$$"""{"version":3,"id":"{{{id}}}","address":"008aeeda4d805471df9b2a5b0f38a0c3bcba786b","crypto":{"ciphertext":"1b368ffb2db0fcec8802eceba0b2820ae1069ef3815ac97ca1031371748f6e18","cipherparams":{"iv":"740770fce12ce862af21264dab25f1da"},"cipher":"aes-128-ctr","kdf":"scrypt","kdfparams":{"dklen":32,"salt":"25710c2ccd7c610b24d068af83b959b7a0e5f40641f0c82daeb1345766191034","n":262144,"r":8,"p":1},"mac":"61de4bf77db2f8e07e01232c045e5b455997efeb22bb8ea08a66ff5a252182a6"}}"""));
        }
    }

    [TestCase("scrypt", "aes-128-ctr", CtrCipherText, CtrMac)]
    [TestCase("Scrypt", "aes-128-ctr", CtrCipherText, CtrMac)]
    [TestCase(" SCRYPT ", "AES-128-CTR", CtrCipherText, CtrMac)]
    [TestCase("scrypt", "aes-128-cbc", CbcCipherText, CbcMac)]
    [TestCase("Scrypt", " Aes-128-Cbc ", CbcCipherText, CbcMac)]
    public void Write_settings_in_any_case_are_written_in_lower_case(string kdf, string cipher, string cipherText, string mac)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        FileKeyStore store = CreateStore(new KeyStoreConfig { KeyStoreDirectory = directory.Path, KdfparamsN = 1024, Kdf = kdf, Cipher = cipher }, FixedRandom());

        KeyStoreItem keyFile = StoreAndReadBack(store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keyFile.Crypto.KDF, Is.EqualTo("scrypt"));
            Assert.That(keyFile.Crypto.Cipher, Is.EqualTo(cipher.Trim().ToLowerInvariant()));
            Assert.That(keyFile.Crypto.CipherText, Is.EqualTo(cipherText));
            Assert.That(keyFile.Crypto.MAC, Is.EqualTo(mac));
        }
    }

    [TestCase(nameof(IKeyStoreConfig.Kdf), "pbkdf2")]
    [TestCase(nameof(IKeyStoreConfig.Cipher), "aes-256-gcm")]
    [TestCase(nameof(IKeyStoreConfig.Cipher), null)]
    [TestCase(nameof(IKeyStoreConfig.KdfparamsDklen), 31)]
    [TestCase(nameof(IKeyStoreConfig.KdfparamsDklen), 64)]
    [TestCase(nameof(IKeyStoreConfig.KdfparamsSaltLen), 15)]
    [TestCase(nameof(IKeyStoreConfig.KdfparamsN), 3)]
    [TestCase(nameof(IKeyStoreConfig.KdfparamsN), 0)]
    [TestCase(nameof(IKeyStoreConfig.KdfparamsR), 0)]
    [TestCase(nameof(IKeyStoreConfig.KdfparamsP), 0)]
    public void Unsupported_write_settings_throw_before_anything_is_written(string setting, object value)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        KeyStoreConfig config = new() { KeyStoreDirectory = directory.Path, KdfparamsN = 1024 };
        ICryptoRandom random = FixedRandom();
        FileKeyStore store = CreateStore(config, random);
        typeof(KeyStoreConfig).GetProperty(setting)!.SetValue(config, value);
        PrivateKey key = new(VectorKey);

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() => store.StoreKey(key, VectorPassword.Secure()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.ExitCode, Is.EqualTo(ExitCodes.ForbiddenOptionValue));
            Assert.That(exception.Message, Does.StartWith($"KeyStore.{setting} '"));
            Assert.That(random.ReceivedCalls(), Is.Empty, "no salt or IV requested");
            Assert.That(store.HasKey(key.Address), Is.False);
        }
    }

    [Test]
    public void Salt_length_setting_is_used([Values(16, 48)] int saltLength)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        FileKeyStore store = CreateStore(new KeyStoreConfig { KeyStoreDirectory = directory.Path, KdfparamsN = 1024, KdfparamsSaltLen = saltLength });

        KeyStoreItem keyFile = StoreAndReadBack(store);

        Assert.That(Bytes.FromHexString(keyFile.Crypto.KDFParams.Salt), Has.Length.EqualTo(saltLength));
    }

    private static IEnumerable<TestCaseData> ReadableKeyFiles()
    {
        yield return new TestCaseData(Parse(Pbkdf2Vector)).SetArgDisplayNames("definition pbkdf2 vector");

        KeyStoreItem otherCase = Parse(Pbkdf2Vector);
        otherCase.Crypto.KDF = "PBKDF2";
        otherCase.Crypto.Cipher = " AES-128-CTR ";
        yield return new TestCaseData(otherCase).SetArgDisplayNames("names in another case");

        byte[] password = Encoding.UTF8.GetBytes(VectorPassword);
        byte[] salt = Bytes.FromHexString(VectorSalt);
        // Longer than the 256 bytes that pbkdf2 key files used to be derived with.
        yield return new TestCaseData(BuildKeyFile(
            "pbkdf2", new KdfParams { C = 2, DkLen = 272, Prf = "hmac-sha256", Salt = VectorSalt },
            Rfc2898DeriveBytes.Pbkdf2(password, salt, 2, HashAlgorithmName.SHA256, 272))).SetArgDisplayNames("pbkdf2 with dklen 272");

        // Below the limits for new key files, which do not apply to reading.
        yield return new TestCaseData(ScryptKeyFile("scrypt", salt[..8], 64)).SetArgDisplayNames("scrypt with an 8-byte salt and dklen 64");
        yield return new TestCaseData(ScryptKeyFile("scrypt", salt, 16)).SetArgDisplayNames("scrypt with dklen 16");
        yield return new TestCaseData(ScryptKeyFile("Scrypt", salt, 32)).SetArgDisplayNames("scrypt named Scrypt");

        KeyStoreItem ScryptKeyFile(string kdf, byte[] kdfSalt, int dkLen) => BuildKeyFile(
            kdf, new KdfParams { N = 1024, R = 8, P = 1, DkLen = dkLen, Salt = kdfSalt.ToHexString() },
            SCrypt.ComputeDerivedKey(password, kdfSalt, 1024, 8, 1, null, dkLen));
    }

    [TestCaseSource(nameof(ReadableKeyFiles))]
    public void Key_files_are_read_with_their_own_parameters(KeyStoreItem keyFile)
    {
        (PrivateKey key, Result result) = Read(keyFile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), result.Error);
            Assert.That(key?.KeyBytes, Is.EqualTo(Bytes.FromHexString(VectorKey)));
        }
    }

    private static IEnumerable<TestCaseData> UnusableKeyFiles()
    {
        yield return Case("pbkdf2 without c", static crypto => crypto.KDFParams.C = null, "Incorrect key: pbkdf2 requires a positive c");
        yield return Case("pbkdf2 with c 0", static crypto => crypto.KDFParams.C = 0, "Incorrect key: pbkdf2 requires a positive c");
        const string invalidScrypt = "Incorrect key: scrypt requires a power-of-two n and positive r and p";
        yield return Case("scrypt without n, r and p", static crypto => crypto.KDF = "scrypt", invalidScrypt);
        yield return Case("scrypt with n 3", Scrypt(3, 8, 1), invalidScrypt);
        yield return Case("scrypt with n 0", Scrypt(0, 8, 1), invalidScrypt);
        yield return Case("scrypt with r 0", Scrypt(1024, 0, 1), invalidScrypt);
        yield return Case("scrypt with p 0", Scrypt(1024, 8, 0), invalidScrypt);
        yield return Case("dklen 15", static crypto => crypto.KDFParams.DkLen = 15, "Unsupported dklen: 15");

        static TestCaseData Case(string name, Action<Crypto> corrupt, string error)
        {
            KeyStoreItem keyFile = Parse(Pbkdf2Vector);
            corrupt(keyFile.Crypto);
            return new TestCaseData(keyFile, error).SetArgDisplayNames(name);
        }

        static Action<Crypto> Scrypt(int n, int r, int p) => crypto =>
        {
            crypto.KDF = "scrypt";
            (crypto.KDFParams.N, crypto.KDFParams.R, crypto.KDFParams.P) = (n, r, p);
        };
    }

    [TestCaseSource(nameof(UnusableKeyFiles))]
    public void Key_files_with_unusable_kdf_parameters_fail_without_throwing(KeyStoreItem keyFile, string error)
    {
        (PrivateKey key, Result result) = Read(keyFile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Error, Is.EqualTo(error));
            Assert.That(key, Is.Null);
        }
    }

    private static FileKeyStore CreateStore(IKeyStoreConfig config, ICryptoRandom random = null) =>
        new(config, new EthereumJsonSerializer(), new AesEncrypter(config, LimboLogs.Instance), random ?? new CryptoRandom(), LimboLogs.Instance, new PrivateKeyStoreIOSettingsProvider(config));

    /// <summary>Returns <see cref="VectorSalt"/> and then <see cref="VectorIv"/>, the order in which a key file is written.</summary>
    private static ICryptoRandom FixedRandom()
    {
        ICryptoRandom random = Substitute.For<ICryptoRandom>();
        random.GenerateRandomBytes(Arg.Any<int>()).Returns(Bytes.FromHexString(VectorSalt), Bytes.FromHexString(VectorIv));
        return random;
    }

    /// <summary>Stores the vector key, checks that it reads back and returns its key file.</summary>
    private static KeyStoreItem StoreAndReadBack(FileKeyStore store)
    {
        PrivateKey key = new(VectorKey);
        Result result = store.StoreKey(key, VectorPassword.Secure());
        Assert.That(result.ResultType, Is.EqualTo(ResultType.Success), result.Error);

        (PrivateKey restored, Result readResult) = store.GetKey(key.Address, VectorPassword.Secure());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(readResult.ResultType, Is.EqualTo(ResultType.Success), readResult.Error);
            Assert.That(restored?.KeyBytes, Is.EqualTo(key.KeyBytes));
        }

        return store.GetKeyData(key.Address).KeyData;
    }

    /// <summary>Reads the vector key from <paramref name="keyFile"/> with write settings that new key files cannot use.</summary>
    private static (PrivateKey Key, Result Result) Read(KeyStoreItem keyFile)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        FileKeyStore store = CreateStore(new KeyStoreConfig { KeyStoreDirectory = directory.Path, Kdf = "pbkdf2", Cipher = "aes-256-gcm", KdfparamsDklen = 64, KdfparamsSaltLen = 8 });
        Address address = new PrivateKey(VectorKey).Address;
        store.StoreKey(address, keyFile);
        return store.GetKey(address, VectorPassword.Secure());
    }

    private static KeyStoreItem Parse(string json) => new EthereumJsonSerializer().Deserialize<KeyStoreItem>(json);

    /// <summary>Builds a key file for the vector key as the store reads one: AES-128-CTR under derived bytes 0..15, MAC over the last 16.</summary>
    private static KeyStoreItem BuildKeyFile(string kdf, KdfParams kdfParams, byte[] derivedKey)
    {
        byte[] cipherText = new AesEncrypter(new KeyStoreConfig(), LimboLogs.Instance).Encrypt(Bytes.FromHexString(VectorKey), derivedKey[..16], Bytes.FromHexString(VectorIv), "aes-128-ctr");
        byte[] macBody = [.. derivedKey[^16..], .. cipherText];
        return new KeyStoreItem
        {
            Version = 3,
            Crypto = new Crypto
            {
                Cipher = "aes-128-ctr",
                CipherText = cipherText.ToHexString(),
                CipherParams = new CipherParams { IV = VectorIv },
                KDF = kdf,
                KDFParams = kdfParams,
                MAC = Keccak.Compute(macBody).BytesToArray().ToHexString()
            }
        };
    }
}

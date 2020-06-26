﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Encryption
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization.Formatters.Binary;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    internal static class PropertyEncryptionProcessor
    {
        internal static readonly CosmosJsonDotNetSerializer BaseSerializer = new CosmosJsonDotNetSerializer();

        /// <remarks>
        /// If there isn't any PathsToEncrypt, input stream will be returned without any modification.
        /// Else input stream will be disposed, and a new stream is returned.
        /// In case of an exception, input stream won't be disposed, but position will be end of stream.
        /// </remarks>
        public static async Task<Stream> EncryptAsync(
            Stream input,
            Encryptor encryptor,
            EncryptionOptions encryptionOptions,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            Debug.Assert(diagnosticsContext != null);

            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (encryptor == null)
            {
                throw new ArgumentNullException(nameof(encryptor));
            }

            if (encryptionOptions == null)
            {
                throw new ArgumentNullException(nameof(encryptionOptions));
            }

            if (string.IsNullOrWhiteSpace(encryptionOptions.DataEncryptionKeyId))
            {
                throw new ArgumentNullException(nameof(encryptionOptions.DataEncryptionKeyId));
            }

            if (string.IsNullOrWhiteSpace(encryptionOptions.EncryptionAlgorithm))
            {
                throw new ArgumentNullException(nameof(encryptionOptions.EncryptionAlgorithm));
            }

            if (encryptionOptions.PathsToEncrypt == null)
            {
                throw new ArgumentNullException(nameof(encryptionOptions.PathsToEncrypt));
            }

            if (!encryptionOptions.PathsToEncrypt.Any())
            {
                return input;
            }

            foreach (string path in encryptionOptions.PathsToEncrypt)
            {
                if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.LastIndexOf('/') != 0)
                {
                    throw new ArgumentException($"Invalid path {path ?? string.Empty}", nameof(encryptionOptions.PathsToEncrypt));
                }
            }

            List<EncryptionProperties> list = new List<EncryptionProperties>();

            JObject itemJObj = EncryptionProcessor.BaseSerializer.FromStream<JObject>(input);

            JObject toEncryptJObj = new JObject();
            if (itemJObj != null)
            {
                foreach (string pathToEncrypt in encryptionOptions.PathsToEncrypt)
                {
                    string propertyName = pathToEncrypt.Substring(1);
                    if (itemJObj.TryGetValue(propertyName, out JToken propertyValue))
                    {
                        string s = propertyValue.Value<string>();
                        byte[] plainText = System.Text.Encoding.UTF8.GetBytes(s);

                        byte[] cipherText = await encryptor.EncryptAsync(
                            plainText,
                            encryptionOptions.DataEncryptionKeyId,
                            encryptionOptions.EncryptionAlgorithm,
                            cancellationToken);

                        if (cipherText == null)
                        {
                            throw new InvalidOperationException($"{nameof(Encryptor)} returned null cipherText from {nameof(EncryptAsync)}.");
                        }

                        EncryptionProperties encryptionProperties = new EncryptionProperties(
                                encryptionFormatVersion: 2,
                                encryptionOptions.EncryptionAlgorithm,
                                encryptionOptions.DataEncryptionKeyId,
                                encryptedData: cipherText,
                                encryptedPaths: propertyName);

                        itemJObj[propertyName] = cipherText;
                        input.Dispose();
                    }

                    toEncryptJObj.Remove(propertyName);
                }
            }

            return EncryptionProcessor.BaseSerializer.ToStream(itemJObj);
        }

        /// <remarks>
        /// If there isn't any data that needs to be decrypted, input stream will be returned without any modification.
        /// Else input stream will be disposed, and a new stream is returned.
        /// In case of an exception, input stream won't be disposed, but position will be end of stream.
        /// </remarks>
        public static async Task<Stream> DecryptAsync(
            Stream input,
            Encryptor encryptor,
            CosmosDiagnosticsContext diagnosticsContext,
            Dictionary<List<string>, string> toEncrypt,
            CancellationToken cancellationToken)
        {
            Debug.Assert(input != null);
            Debug.Assert(input.CanSeek);
            Debug.Assert(encryptor != null);
            Debug.Assert(diagnosticsContext != null);

            JObject itemJObj;
            using (StreamReader sr = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
            {
                using JsonTextReader jsonTextReader = new JsonTextReader(sr);
                itemJObj = JsonSerializer.Create().Deserialize<JObject>(jsonTextReader);
            }

            foreach (List<string> paths in toEncrypt.Keys)
            {
                foreach (string path in paths)
                {
                    if (itemJObj.TryGetValue(path.Substring(1), out JToken propertyValue))
                    {
                        EncryptionProperties encryptionProperties = new EncryptionProperties(
                                    encryptionFormatVersion: 2,
                                    CosmosEncryptionAlgorithm.AEAD_AES_256_CBC_HMAC_SHA256,
                                    toEncrypt[paths],
                                    propertyValue.ToObject<byte[]>(),
                                    path);

                        JObject propPlainTextJObj = await PropertyEncryptionProcessor.DecryptContentAsync(
                        encryptionProperties,
                        encryptor,
                        diagnosticsContext,
                        cancellationToken);

                        foreach (JProperty property in propPlainTextJObj.Properties())
                        {
                            itemJObj.Remove(property.Name);
                            itemJObj[property.Name] = property.Value;
                        }
                    }
                }

                input.Dispose();
            }

            JToken token = itemJObj[Constants.EncryptedInfo];
            if (token != null)
            {
                JProperty encryptionPropertiesJProp = itemJObj.Property(Constants.EncryptedInfo);
                JObject encryptionPropertiesJObj = null;
                if (encryptionPropertiesJProp != null && encryptionPropertiesJProp.Value != null && encryptionPropertiesJProp.Value.Type == JTokenType.Object)
                {
                    encryptionPropertiesJObj = (JObject)encryptionPropertiesJProp.Value;
                }

                if (encryptionPropertiesJObj == null)
                {
                    input.Position = 0;
                    return input;
                }

                EncryptionProperties encryptionProperties = encryptionPropertiesJObj.ToObject<EncryptionProperties>();

                JObject plainTextJObj = await PropertyEncryptionProcessor.DecryptContentAsync(
                    encryptionProperties,
                    encryptor,
                    diagnosticsContext,
                    cancellationToken);

                foreach (JProperty property in plainTextJObj.Properties())
                {
                    itemJObj.Add(property.Name, property.Value);
                }

                itemJObj.Remove(Constants.EncryptedInfo);
                input.Dispose();
            }

            return EncryptionProcessor.BaseSerializer.ToStream(itemJObj);
        }

        public static async Task<JObject> DecryptAsync(
            JObject document,
            Encryptor encryptor,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken,
            Dictionary<List<string>, string> toEncrypt)
        {
            Debug.Assert(document != null);
            Debug.Assert(encryptor != null);
            Debug.Assert(diagnosticsContext != null);

            foreach (List<string> paths in toEncrypt.Keys)
            {
                foreach (string path in paths)
                {
                    if (document.TryGetValue(path.Substring(1), out JToken propertyValue))
                    {
                        EncryptionProperties encryptionProperties = new EncryptionProperties(
                                    encryptionFormatVersion: 2,
                                    CosmosEncryptionAlgorithm.AEAD_AES_256_CBC_HMAC_SHA256,
                                    toEncrypt[paths],
                                    propertyValue.ToObject<byte[]>(),
                                    path);

                        JObject propPlainTextJObj = await PropertyEncryptionProcessor.DecryptContentAsync(
                        encryptionProperties,
                        encryptor,
                        diagnosticsContext,
                        cancellationToken);
                        foreach (JProperty property in propPlainTextJObj.Properties())
                        {
                            document[property.Name] = property.Value;
                        }
                    }
                }
            }

            if (document.TryGetValue(Constants.EncryptedInfo, out JToken encryptedInfo))
            {
                EncryptionProperties encryptionProperties = JsonConvert.DeserializeObject<EncryptionProperties>(encryptedInfo.ToString());

                JObject plainTextJObj = await PropertyEncryptionProcessor.DecryptContentAsync(
                    encryptionProperties,
                    encryptor,
                    diagnosticsContext,
                    cancellationToken);

                document.Remove(Constants.EncryptedInfo);

                foreach (JProperty property in plainTextJObj.Properties())
                {
                    document.Add(property.Name, property.Value);
                }
            }

            return document;
        }

        private static async Task<JObject> DecryptContentAsync(
            EncryptionProperties encryptionProperties,
            Encryptor encryptor,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            if (encryptionProperties.EncryptionFormatVersion != 2)
            {
                throw new NotSupportedException($"Unknown encryption format version: {encryptionProperties.EncryptionFormatVersion}. Please upgrade your SDK to the latest version.");
            }

            byte[] plainText = await encryptor.DecryptAsync(
                encryptionProperties.EncryptedData,
                encryptionProperties.DataEncryptionKeyId,
                encryptionProperties.EncryptionAlgorithm,
                cancellationToken);

            if (plainText == null)
            {
                throw new InvalidOperationException($"{nameof(Encryptor)} returned null plainText from {nameof(DecryptAsync)}.");
            }

            if (encryptionProperties.EncryptedPaths != null)
            {
                string val = System.Text.Encoding.UTF8.GetString(plainText);

                JObject plainTextJObj = new JObject();
                string key = encryptionProperties.EncryptedPaths.Substring(1);
                plainTextJObj.Add(key, val);

                return plainTextJObj;
            }
            else
            {
                JObject plainTextJObj;
                using (MemoryStream memoryStream = new MemoryStream(plainText))
                using (StreamReader streamReader = new StreamReader(memoryStream))
                using (JsonTextReader jsonTextReader = new JsonTextReader(streamReader))
                {
                    plainTextJObj = JObject.Load(jsonTextReader);
                }

                return plainTextJObj;
            }
        }
    }
}
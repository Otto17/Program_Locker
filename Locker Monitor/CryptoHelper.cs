// Copyright (c) 2025-2026 Otto
// Лицензия: MIT (см. LICENSE)

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Locker_Monitor
{
    public static class CryptoHelper
    {
        // DecryptEntries расшифровывает список записей
        public static List<FileEntry> DecryptEntries(EncryptedBlob blob, string password)
        {
            if (blob == null) return [];

            byte[] salt = Convert.FromBase64String(blob.SaltBase64);
            byte[] iv = Convert.FromBase64String(blob.IvBase64);
            byte[] cipher = Convert.FromBase64String(blob.CiphertextBase64);
            byte[] hmacStored = Convert.FromBase64String(blob.HmacBase64);
            int iterations = blob.Iterations <= 0 ? 20000 : blob.Iterations; // Устанавливает 20000 итераций по умолчанию

            using Rfc2898DeriveBytes kdf = new(password, salt, iterations, HashAlgorithmName.SHA256);
            byte[] key = kdf.GetBytes(32);      // Генерирует ключ для AES
            byte[] hmacKey = kdf.GetBytes(32);  // Генерирует ключ для HMAC

            byte[] toH = Concat(salt, iv, cipher);
            using (HMACSHA256 h = new(hmacKey))
            {
                byte[] computed = h.ComputeHash(toH);
                if (!ConstantTimeEquals(computed, hmacStored))
                    throw new CryptographicException("HMAC mismatch"); // Сверяет HMAC для проверки целостности данных
            }

            using Aes aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;

            byte[] plain;
            using (MemoryStream ms = new())
            using (CryptoStream cs = new(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
            {
                cs.Write(cipher, 0, cipher.Length);
                cs.FlushFinalBlock();
                plain = ms.ToArray();
            }

            string json = Encoding.UTF8.GetString(plain); // Преобразует расшифрованные данные в строку JSON
            return JsonConvert.DeserializeObject<List<FileEntry>>(json) ?? [];
        }

        // EncryptEntries шифрует список записей
        public static EncryptedBlob EncryptEntries(List<FileEntry> entries, string password)
        {
            string plain = JsonConvert.SerializeObject(entries);
            byte[] plainBytes = Encoding.UTF8.GetBytes(plain);

            byte[] salt = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt); // Генерирует криптографически стойкую соль
            }
            int iterations = 20000; // Использует фиксированное количество итераций

            using Rfc2898DeriveBytes kdf = new(password, salt, iterations, HashAlgorithmName.SHA256);
            byte[] key = kdf.GetBytes(32);      // Генерирует ключ для AES
            byte[] hmacKey = kdf.GetBytes(32);  // Генерирует ключ для HMAC

            using Aes aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.GenerateIV();
            byte[] iv = aes.IV;

            byte[] cipher;
            using (MemoryStream ms = new())
            using (CryptoStream cs = new(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(plainBytes, 0, plainBytes.Length);
                cs.FlushFinalBlock();
                cipher = ms.ToArray();
            }

            byte[] toH = Concat(salt, iv, cipher);
            byte[] hmac;
            using (HMACSHA256 h = new(hmacKey))
            {
                hmac = h.ComputeHash(toH); // Вычисляет HMAC для обеспечения целостности
            }

            return new EncryptedBlob
            {
                SaltBase64 = Convert.ToBase64String(salt),
                IvBase64 = Convert.ToBase64String(iv),
                CiphertextBase64 = Convert.ToBase64String(cipher),
                HmacBase64 = Convert.ToBase64String(hmac),
                Iterations = iterations
            };
        }

        // Concat объединяет массивы байтов в один
        private static byte[] Concat(params byte[][] parts)
        {
            int len = parts.Sum(p => p.Length);
            byte[] r = new byte[len];
            int pos = 0;
            foreach (byte[] p in parts)
            {
                Buffer.BlockCopy(p, 0, r, pos, p.Length);
                pos += p.Length;
            }
            return r;
        }

        // ConstantTimeEquals выполняет сравнение массивов за постоянное время
        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
// Copyright (c) 2025-2026 Otto
// Лицензия: MIT (см. LICENSE)

using System;
using System.Collections.Generic;
using System.IO;

namespace Locker_Monitor
{
    // ProtectionHelper предоставляет методы для патчинга исполняемых файлов
    public static class ProtectionHelper
    {
        private static readonly Random Rng = new();     // Генератор случайных чисел
        private static readonly object RngLock = new(); // Объект для синхронизации доступа к генератору

        // ApplyProtection применяет заплатки к PE-файлу, записывая случайные байты
        public static List<PatchInfo> ApplyProtection(string filePath)
        {
            var patches = new List<PatchInfo>();

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 0x40)
                    throw new InvalidOperationException("Файл слишком маленький для PE"); // Требует минимального размера для чтения PE-заголовка

                fs.Seek(0x3C, SeekOrigin.Begin); // Переходит к смещению для чтения e_lfanew
                uint e_lfanew = br.ReadUInt32();

                if (e_lfanew + 0x78 > fs.Length)
                    throw new InvalidOperationException("Некорректный PE-заголовок"); // Проверяет корректность смещения PE-заголовка

                // AEP offset
                long aepOffset = e_lfanew + 4 + 20 + 16; // Вычисляет смещение Address of Entry Point (AEP)
                fs.Seek(aepOffset, SeekOrigin.Begin);
                uint entryRva = br.ReadUInt32();

                // Patch AEP
                patches.Add(PatchLocation(fs, aepOffset, 4, "AEP")); // Патчит AEP, чтобы предотвратить запуск приложения напрямую

                // Entry code
                long entryFileOffset = RvaToFileOffset(fs, br, e_lfanew, entryRva);
                if (entryFileOffset > 0 && entryFileOffset + 16 <= fs.Length)
                    patches.Add(PatchLocation(fs, entryFileOffset, 16, "EntryCode"));

                // Import directory
                long importDirOffset = e_lfanew + 4 + 20 + 96 + 8;
                if (importDirOffset + 8 <= fs.Length)
                    patches.Add(PatchLocation(fs, importDirOffset, 8, "ImportDir")); // Патчит директорию импорта, чтобы нарушить загрузку зависимостей

                // Checksum
                long checksumOffset = e_lfanew + 4 + 20 + 64;
                if (checksumOffset + 4 <= fs.Length)
                    patches.Add(PatchLocation(fs, checksumOffset, 4, "Checksum")); // Патчит контрольную сумму, чтобы файл выглядел модифицированным
            }

            return patches;
        }

        // PatchLocation записывает случайные байты в указанное место и сохраняет оригинальные данные
        private static PatchInfo PatchLocation(FileStream fs, long offset, int length, string type)
        {
            byte[] original = new byte[length];
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Read(original, 0, length);

            byte[] garbage = new byte[length];
            lock (RngLock)
            {
                Rng.NextBytes(garbage); // Синхронизирует доступ к Rng, так как он не потокобезопасен
            }

            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(garbage, 0, length); // Записывает случайные данные, чтобы заблокировать оригинальную функцию

            return new PatchInfo
            {
                Offset = offset,
                Length = length,
                OriginalBytesBase64 = Convert.ToBase64String(original),
                Type = type
            };
        }

        // RvaToFileOffset преобразует относительный виртуальный адрес (RVA) в физическое смещение в файле
        private static long RvaToFileOffset(FileStream fs, BinaryReader br, uint e_lfanew, uint rva)
        {
            try
            {
                fs.Seek(e_lfanew + 4 + 2, SeekOrigin.Begin);
                ushort numberOfSections = br.ReadUInt16();

                fs.Seek(e_lfanew + 4 + 16, SeekOrigin.Begin);
                ushort sizeOfOptionalHeader = br.ReadUInt16();

                long sectionTableOffset = e_lfanew + 4 + 20 + sizeOfOptionalHeader; // Вычисляет смещение к таблице секций

                for (int i = 0; i < numberOfSections; i++)
                {
                    long sectionOffset = sectionTableOffset + (i * 40);
                    fs.Seek(sectionOffset + 12, SeekOrigin.Begin);
                    uint virtualAddress = br.ReadUInt32();
                    uint sizeOfRawData = br.ReadUInt32();
                    uint pointerToRawData = br.ReadUInt32();

                    fs.Seek(sectionOffset + 8, SeekOrigin.Begin);
                    uint virtualSize = br.ReadUInt32();

                    // Проверяет, попадает ли RVA в текущую секцию
                    if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, sizeOfRawData))
                    {
                        return pointerToRawData + (rva - virtualAddress);
                    }
                }
            }
            catch { }

            return -1;
        }
    }
}
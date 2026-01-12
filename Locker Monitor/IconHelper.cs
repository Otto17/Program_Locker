// Copyright (c) 2025-2026 Otto
// Лицензия: MIT (см. LICENSE)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Locker_Monitor
{
    // IconHelper предоставляет функциональность для замены иконок в исполняемых файлах
    public static class IconHelper
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpType, EnumResNameProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, IntPtr lParam);

        private static readonly IntPtr RT_ICON = new(3);        // Идентификатор типа ресурса ICON
        private static readonly IntPtr RT_GROUP_ICON = new(14); // Идентификатор типа ресурса GROUP_ICON

        // ReplaceIcon заменяет иконку в целевом файле иконкой из исходного
        public static bool ReplaceIcon(string sourceExe, string targetExe)
        {
            IntPtr hSource = IntPtr.Zero;
            IntPtr hUpdate = IntPtr.Zero;

            try
            {
                hSource = LoadLibraryEx(sourceExe, IntPtr.Zero, 0x00000002); // Загружает исходный PE-файл как модуль данных
                if (hSource == IntPtr.Zero) return false;

                var groupIconIds = new List<IntPtr>();
                var iconIds = new List<IntPtr>();

                // Перечисляет идентификаторы групповых иконок
                EnumResourceNames(hSource, RT_GROUP_ICON, (hMod, lpType, lpName, lParam) =>
                {
                    groupIconIds.Add(lpName);
                    return true;
                }, IntPtr.Zero);

                // Перечисляет идентификаторы иконок
                EnumResourceNames(hSource, RT_ICON, (hMod, lpType, lpName, lParam) =>
                {
                    iconIds.Add(lpName);
                    return true;
                }, IntPtr.Zero);

                if (groupIconIds.Count == 0) return false; // Возвращает ошибку если исходный файл не содержит групповых иконок

                hUpdate = BeginUpdateResource(targetExe, false);
                if (hUpdate == IntPtr.Zero) return false; // Возвращает ошибку если не удается начать обновление ресурсов

                foreach (var id in groupIconIds)
                {
                    byte[] data = ExtractResource(hSource, RT_GROUP_ICON, id);
                    if (data != null)
                    {
                        // Использует IDI_APPLICATION (32512) для первой групповой иконки
                        IntPtr targetId = (groupIconIds.IndexOf(id) == 0) ? new IntPtr(32512) : id;
                        UpdateResource(hUpdate, RT_GROUP_ICON, targetId, 0, data, (uint)data.Length);
                    }
                }

                foreach (var id in iconIds)
                {
                    byte[] data = ExtractResource(hSource, RT_ICON, id);
                    if (data != null)
                    {
                        UpdateResource(hUpdate, RT_ICON, id, 0, data, (uint)data.Length);
                    }
                }

                return EndUpdateResource(hUpdate, false);
            }
            catch
            {
                if (hUpdate != IntPtr.Zero)
                    EndUpdateResource(hUpdate, true); // Отменяет изменения в случае ошибки
                return false;
            }
            finally
            {
                if (hSource != IntPtr.Zero)
                    FreeLibrary(hSource);
            }
        }

        // ExtractResource извлекает необработанные данные ресурса по типу и имени
        private static byte[] ExtractResource(IntPtr hModule, IntPtr type, IntPtr name)
        {
            try
            {
                IntPtr hResInfo = FindResource(hModule, name, type);
                if (hResInfo == IntPtr.Zero) return null;

                uint size = SizeofResource(hModule, hResInfo);
                if (size == 0) return null;

                IntPtr hResData = LoadResource(hModule, hResInfo);
                if (hResData == IntPtr.Zero) return null;

                IntPtr pData = LockResource(hResData);
                if (pData == IntPtr.Zero) return null;

                byte[] data = new byte[size];
                Marshal.Copy(pData, data, 0, (int)size);
                return data;
            }
            catch
            {
                return null;
            }
        }
    }
}
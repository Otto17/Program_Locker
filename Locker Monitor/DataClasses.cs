// Copyright (c) 2025-2026 Otto
// Лицензия: MIT (см. LICENSE)

using System;
using System.Collections.Generic;

namespace Locker_Monitor
{
    // MonitorTask представляет задачу мониторинга
    public class MonitorTask
    {
        public string VisiblePath { get; set; }
        public string HiddenPath { get; set; }
        public string EncryptedPasswordBase64 { get; set; }
        public bool NoLauncher { get; set; }
        public DateTime AddedAtUtc { get; set; }
        public int FailCount { get; set; }
        public string UserSid { get; set; } // Идентификатор SID пользователя
    }

    // ConfigStore хранит глобальную конфигурацию Program Locker
    public class ConfigStore
    {
        public string MasterSaltBase64 { get; set; }
        public string MasterHashBase64 { get; set; }
        public string ProgLocExePath { get; set; }
        public int MasterIterations { get; set; }
        public EncryptedBlob EntriesBlob { get; set; }
    }

    // FileEntry представляет запись о защищенном файле
    public class FileEntry
    {
        public string VisiblePath { get; set; }
        public string HiddenPath { get; set; }
        public DateTime TimestampUtc { get; set; }
        public List<PatchInfo> Patches { get; set; } = [];
        public bool NoLauncher { get; set; }
    }

    // PatchInfo хранит информацию о внесенном патче
    public class PatchInfo
    {
        public long Offset { get; set; }
        public string OriginalBytesBase64 { get; set; }
        public int Length { get; set; }
        public string Type { get; set; }
    }

    // EncryptedBlob содержит все компоненты зашифрованных данных
    public class EncryptedBlob
    {
        public string SaltBase64 { get; set; }
        public string IvBase64 { get; set; }
        public string CiphertextBase64 { get; set; }
        public string HmacBase64 { get; set; }
        public int Iterations { get; set; }
    }
}
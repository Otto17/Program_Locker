// Copyright (c) 2025-2026 Otto
// Лицензия: MIT (см. LICENSE)

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Locker_Monitor
{
    // MonitorEngine выполняет отслеживание разблокированных программ
    public static class MonitorEngine
    {
        // Задачи хранятся в "C:\ProgramData\Program Locker"
        static readonly string AppFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Program Locker");
        static readonly string MonitorTasksPath = Path.Combine(AppFolder, "monitor_tasks.json");

        private static Thread monitorThread;
        private static volatile bool isRunning;
        private static readonly object lockObj = new();

        // IsRunning возвращает текущее состояние работы движка
        public static bool IsRunning => isRunning;

        #region Service Control

        // Start запускает движок мониторинга
        public static void Start()
        {
            lock (lockObj)
            {
                if (isRunning) return;

                isRunning = true;

                try
                {
                    Directory.CreateDirectory(AppFolder);
                }
                catch { } // Игнорирует ошибки при создании каталога, так как он может существовать

                monitorThread = new Thread(MonitorLoop)
                {
                    IsBackground = true,
                    Name = "MonitorThread",
                    Priority = ThreadPriority.AboveNormal // Повышает приоритет, чтобы быстрее реагировать на закрытие процессов
                };
                monitorThread.Start();
            }
        }

        // Stop останавливает движок мониторинга
        public static void Stop()
        {
            lock (lockObj)
            {
                isRunning = false;
            }

            if (monitorThread != null && monitorThread.IsAlive)
            {
                monitorThread.Join(TimeSpan.FromSeconds(10)); // Ожидает завершения потока, чтобы избежать обрыва операций
            }
        }

        #endregion

        #region Monitor Loop

        // MonitorLoop содержит основной цикл мониторинга
        private static void MonitorLoop()
        {
            while (isRunning)
            {
                try
                {
                    ProcessMonitorTasks();
                }
                catch (Exception)
                {
                    // Игнорирует исключения в главном цикле, чтобы не прерывать работу службы
                }

                for (int i = 0; i < 20 && isRunning; i++)
                {
                    Thread.Sleep(100); // Ожидает 2 секунды перед следующей проверкой
                }
            }
        }

        // ProcessMonitorTasks обрабатывает все задачи мониторинга
        private static void ProcessMonitorTasks()
        {
            List<MonitorTask> tasks = LoadMonitorTasks();

            if (tasks.Count == 0)
                return;

            List<string> tasksToRemove = [];

            foreach (MonitorTask task in tasks)
            {
                if (!isRunning) break;

                try
                {
                    string exeName = Path.GetFileNameWithoutExtension(task.VisiblePath);
                    bool isRunningProcess = IsProcessRunning(task.VisiblePath, exeName);

                    if (!isRunningProcess)
                    {
                        if (WaitForStableState(task.VisiblePath, exeName))
                        {
                            bool success = RestoreProtection(task);

                            if (success)
                            {
                                tasksToRemove.Add(task.VisiblePath); // Успешное восстановление означает, что задачу можно удалить
                            }
                            else
                            {
                                task.FailCount++;

                                if (task.FailCount > 10)
                                {
                                    tasksToRemove.Add(task.VisiblePath); // Удаляет задачу после многократных неудачных попыток
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Игнорирует ошибки обработки одной задачи, чтобы не заблокировать остальные
                }
            }

            foreach (string path in tasksToRemove)
            {
                RemoveMonitorTaskInternal(path);
            }

            // Сохраняет обновлённые счётчики ошибок
            List<MonitorTask> remainingTasks = [.. tasks.Where(t => !tasksToRemove.Contains(t.VisiblePath))];
            if (remainingTasks.Any(t => t.FailCount > 0))
            {
                SaveMonitorTasks(remainingTasks);
            }
        }

        #endregion

        #region Process Monitoring

        // IsProcessRunning проверяет, запущен ли процесс с указанным путём
        private static bool IsProcessRunning(string exePath, string exeName)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(exeName);

                foreach (Process p in processes)
                {
                    try
                    {
                        string processPath = null;
                        try
                        {
                            processPath = p.MainModule?.FileName;
                        }
                        catch
                        {
                            return true; // Если не удается получить путь, считаем процесс запущенным, чтобы избежать ложной сработки
                        }

                        if (processPath != null &&
                            string.Equals(processPath, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch { }

            return false;
        }

        // WaitForStableState ждёт стабильного состояния файла (не запущен и не модифицируется)
        private static bool WaitForStableState(string exePath, string exeName)
        {
            const int stabilizationChecks = 3;
            const int checkIntervalMs = 1000;
            const int maxTotalWaitMs = 30000;

            int totalWaited = 0;
            int stableCount = 0;
            DateTime lastModified = DateTime.MinValue;
            long lastSize = 0;

            if (File.Exists(exePath))
            {
                try
                {
                    lastModified = File.GetLastWriteTimeUtc(exePath);
                    lastSize = new FileInfo(exePath).Length;
                }
                catch { }
            }

            while (totalWaited < maxTotalWaitMs && isRunning)
            {
                Thread.Sleep(checkIntervalMs);
                totalWaited += checkIntervalMs;

                bool processRunning = IsProcessRunning(exePath, exeName);
                bool fileExists = File.Exists(exePath);
                DateTime currentModified = DateTime.MinValue;
                long currentSize = 0;

                if (fileExists)
                {
                    try
                    {
                        currentModified = File.GetLastWriteTimeUtc(exePath);
                        currentSize = new FileInfo(exePath).Length;
                    }
                    catch
                    {
                        stableCount = 0; // Сброс счётчика, если файл недоступен для проверки
                        continue;
                    }
                }

                bool isStable = !processRunning &&
                               fileExists &&
                               currentModified == lastModified &&
                               currentSize == lastSize;

                if (isStable)
                {
                    stableCount++;
                    if (stableCount >= stabilizationChecks)
                        return true; // Файл стабилен и не модифицируется
                }
                else
                {
                    stableCount = 0; // Сброс, если обнаружена модификация или запуск
                }

                lastModified = currentModified;
                lastSize = currentSize;
            }

            return false;
        }

        #endregion

        #region Protection Restoration

        // RestoreProtection восстанавливает защиту для файла, перемещая его и устанавливая лаунчер
        private static bool RestoreProtection(MonitorTask task)
        {
            try
            {
                // Получает путь к конфигу пользователя
                string userConfigPath = GetUserConfigPath(task.UserSid);

                if (string.IsNullOrEmpty(userConfigPath) || !File.Exists(userConfigPath))
                {
                    return false;
                }

                // Расшифровывает пароль
                string password;
                try
                {
                    byte[] encryptedBytes = Convert.FromBase64String(task.EncryptedPasswordBase64);

                    // Использует DPAPI с областью LocalMachine, чтобы пароль был доступен только службе
                    byte[] passwordBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.LocalMachine);
                    password = Encoding.UTF8.GetString(passwordBytes);
                }
                catch (Exception)
                {
                    return false;
                }

                // Загружает конфиг
                string configJson = File.ReadAllText(userConfigPath, Encoding.UTF8);
                ConfigStore config = JsonConvert.DeserializeObject<ConfigStore>(configJson);

                if (config?.EntriesBlob == null)
                    return false;

                // Расшифровывает entries
                List<FileEntry> entries = CryptoHelper.DecryptEntries(config.EntriesBlob, password);

                FileEntry entry = entries.FirstOrDefault(e =>
                    string.Equals(e.VisiblePath, task.VisiblePath, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    return false; // Запись о файле не найдена в конфиге
                }

                string visiblePath = task.VisiblePath;
                string hiddenPath = task.HiddenPath;

                if (!File.Exists(visiblePath))
                {
                    return false; // Файл должен существовать в видимом месте для восстановления защиты
                }

                // Применяет защиту (патчит бинарник)
                entry.Patches = ProtectionHelper.ApplyProtection(visiblePath);

                if (task.NoLauncher)
                {
                    // Режим без лаунчера
                    if (File.Exists(hiddenPath))
                        File.Delete(hiddenPath);        // Удаляет старый скрытый файл
                    File.Move(visiblePath, hiddenPath); // Перемещает пропатченный бинарник в скрытое место
                }
                else
                {
                    // Обычный режим
                    if (File.Exists(hiddenPath))
                        File.Delete(hiddenPath);        // Удаляет старый скрытый файл
                    File.Move(visiblePath, hiddenPath); // Перемещает пропатченный бинарник

                    string launcherPath = Path.Combine(
                        Path.GetDirectoryName(config.ProgLocExePath),
                        "Locker Launcher.exe");

                    if (File.Exists(launcherPath))
                    {
                        File.Copy(launcherPath, visiblePath, true);      // Копирует лаунчер на место пропатченного файла
                        IconHelper.ReplaceIcon(hiddenPath, visiblePath); // Устанавливает оригинальную иконку
                    }
                }

                // Сохраняет обновлённый конфиг
                config.EntriesBlob = CryptoHelper.EncryptEntries(entries, password);
                string newConfigJson = JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(userConfigPath, newConfigJson, Encoding.UTF8);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // GetUserConfigPath получает путь к конфигу пользователя по его SID через реестр
        private static string GetUserConfigPath(string userSid)
        {
            try
            {
                if (string.IsNullOrEmpty(userSid))
                    return null;

                using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + userSid);
                if (key != null)
                {
                    string profilePath = key.GetValue("ProfileImagePath") as string;
                    if (!string.IsNullOrEmpty(profilePath))
                    {
                        // Формирует стандартный путь к файлу конфигурации
                        return Path.Combine(profilePath, "AppData", "Roaming", "Program Locker", "config.json");
                    }
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region Task Management

        // LoadMonitorTasks загружает список задач мониторинга из файла
        private static List<MonitorTask> LoadMonitorTasks()
        {
            lock (lockObj)
            {
                try
                {
                    if (!File.Exists(MonitorTasksPath))
                        return [];

                    string json = File.ReadAllText(MonitorTasksPath, Encoding.UTF8);
                    return JsonConvert.DeserializeObject<List<MonitorTask>>(json) ?? [];
                }
                catch
                {
                    return [];
                }
            }
        }

        // SaveMonitorTasks сохраняет список задач мониторинга в файл
        private static void SaveMonitorTasks(List<MonitorTask> tasks)
        {
            lock (lockObj)
            {
                try
                {
                    Directory.CreateDirectory(AppFolder);
                    string json = JsonConvert.SerializeObject(tasks, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(MonitorTasksPath, json, Encoding.UTF8);
                }
                catch { }
            }
        }

        // AddMonitorTask добавляет задачу мониторинга
        public static void AddMonitorTask(string visiblePath, string hiddenPath, string encryptedPassword, bool noLauncher)
        {
            try
            {
                List<MonitorTask> tasks = LoadMonitorTasks();

                // Удаляет старую запись, чтобы обновить ее новой
                tasks.RemoveAll(t => string.Equals(t.VisiblePath, visiblePath, StringComparison.OrdinalIgnoreCase));

                // Получает SID текущего пользователя, который добавил задачу
                string userSid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value;

                tasks.Add(new MonitorTask
                {
                    VisiblePath = visiblePath,
                    HiddenPath = hiddenPath,
                    EncryptedPasswordBase64 = encryptedPassword,
                    NoLauncher = noLauncher,
                    AddedAtUtc = DateTime.UtcNow,
                    FailCount = 0,
                    UserSid = userSid
                });

                SaveMonitorTasks(tasks);
            }
            catch (Exception)
            {
                // Игнорирует ошибки сохранения
            }
        }

        // RemoveMonitorTask удаляет задачу мониторинга
        public static void RemoveMonitorTask(string visiblePath)
        {
            RemoveMonitorTaskInternal(visiblePath);
        }

        // RemoveMonitorTaskInternal выполняет внутреннее удаление задачи
        private static void RemoveMonitorTaskInternal(string visiblePath)
        {
            try
            {
                List<MonitorTask> tasks = LoadMonitorTasks();
                int removed = tasks.RemoveAll(t =>
                    string.Equals(t.VisiblePath, visiblePath, StringComparison.OrdinalIgnoreCase));

                if (removed > 0)
                {
                    SaveMonitorTasks(tasks);
                }
            }
            catch { }
        }

        // ShowStatus отображает текущий статус задач мониторинга
        public static void ShowStatus()
        {
            List<MonitorTask> tasks = LoadMonitorTasks();

            Console.WriteLine("Active monitor tasks: " + tasks.Count);
            Console.WriteLine("Tasks file: " + MonitorTasksPath);
            Console.WriteLine();

            foreach (MonitorTask task in tasks)
            {
                string exeName = Path.GetFileNameWithoutExtension(task.VisiblePath);
                bool running = IsProcessRunning(task.VisiblePath, exeName);

                Console.WriteLine("  " + Path.GetFileName(task.VisiblePath));
                Console.WriteLine("    Path: " + task.VisiblePath);
                Console.WriteLine("    Running: " + (running ? "Yes" : "No"));
                Console.WriteLine("    NoLauncher: " + task.NoLauncher);
                Console.WriteLine("    Added: " + task.AddedAtUtc.ToString("u"));
                Console.WriteLine("    FailCount: " + task.FailCount);
                Console.WriteLine();
            }
        }
        #endregion
    }
}
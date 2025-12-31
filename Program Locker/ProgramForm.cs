// Copyright (c) 2025 Otto
// Лицензия: MIT (см. LICENSE)

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Drawing;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;

namespace Program_Locker
{
    public partial class ProgramForm : Form
    {
        // Путь к папке с конфигом "%AppData%\Program Locker"
        static readonly string AppFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Program Locker");
        static readonly string ConfigPath = Path.Combine(AppFolder, "config.json"); // Сам конфиг
        static readonly string CurrentExePath = Application.ExecutablePath; // Путь к самой программе (Program Locker.exe)

        ConfigStore store;  // Хранит загруженные настройки приложения

        // Список активных задач мониторинга и флаг принудительного закрытия
        private readonly List<System.Threading.Tasks.Task> activeMonitorTasks = [];
        private readonly object monitorTasksLock = new();
        private bool forceClose = false;

        // Отложенная проверка пароля
        private System.Windows.Forms.Timer passwordCheckTimer;
        private string cachedValidPassword = null;
        private bool isPasswordValid = false;

        // ProgramForm инициализирует компоненты, загружает настройки и строит интерфейс
        public ProgramForm()
        {
            InitializeComponent();
            TryLoadStore();
            BuildFullUI();
        }

        // RunAsUnlocker выполняет разблокировку и запуск указанного файла из командной строки
        public void RunAsUnlocker(string fakeExePath, string password)
        {
            try
            {
                List<FileEntry> entries;
                try
                {
                    // Дешифрует записи, чтобы проверить пароль
                    entries = store.DecryptEntries(password);
                }
                catch (CryptographicException)
                {
                    MessageBox.Show("Неверный пароль.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                catch
                {
                    MessageBox.Show("Ошибка расшифровки конфига.", "Ошибка");
                    return;
                }

                string fullFake = Path.GetFullPath(fakeExePath);

                // Ищет запись по полному пути, чтобы убедиться, что файл контролируется программой
                var entry = entries.FirstOrDefault(e => string.Equals(e.VisiblePath, fullFake, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    MessageBox.Show("Запись не найдена.");
                    return;
                }

                string visiblePath = fullFake;
                string hiddenPath = entry.HiddenPath;

                // Проверяет существование скрытого файла, потому что без него невозможно восстановить оригинал
                if (!File.Exists(hiddenPath))
                {
                    MessageBox.Show(
                        "Оригинальный файл не найден.\n" +
                        "Возможно, система находится в некорректном состоянии после предыдущего сбоя.\n\n" +
                        "Попробуйте разблокировать файл через основной интерфейс Program Locker.exe.",
                        "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Удаляет фейковый файл (лаунчер), чтобы освободить место для оригинала
                if (File.Exists(visiblePath))
                {
                    File.Delete(visiblePath);
                }

                // Перемещает оригинал на место фейкового
                File.Move(hiddenPath, visiblePath);

                // Восстанавливает все патчи 
                ProtectionEngine.RemoveProtection(visiblePath, entry.Patches);

                // Запускает программу
                Process.Start(visiblePath);

                // Ждёт пока ВСЕ процессы этой программы завершатся
                string exeName = Path.GetFileNameWithoutExtension(visiblePath);
                System.Threading.Thread.Sleep(1000);

                // Сохраняет информацию о файле ДО запуска
                DateTime originalModified = File.GetLastWriteTimeUtc(visiblePath);
                long originalSize = new FileInfo(visiblePath).Length;

                WaitForAllProcessInstances(visiblePath, exeName);

                // Период стабилизации — ждёт и проверяет, не перезапустился ли процесс (обновление)
                if (!WaitForStableState(visiblePath, exeName, originalModified, originalSize, out bool fileChanged))
                {
                    // Таймаут — процесс всё ещё работает
                    MessageBox.Show(
                        "Программа всё ещё работает или обновляется.\n" +
                        "Защита не восстановлена.\n\n" +
                        "Закройте программу и заблокируйте её вручную через Program Locker.",
                        "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Если файл не существует — ничего не делает
                if (!File.Exists(visiblePath))
                {
                    MessageBox.Show(
                        "Файл не найден после закрытия программы.\n" +
                        "Возможно, программа была удалена или перемещена.\n\n" +
                        "Запись удалена из защиты.",
                        "Файл не найден", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    entries.Remove(entry);
                    store.EncryptAndStoreEntries(entries, password);
                    SaveStore();
                    return;
                }

                // После завершения всех копий — пытается восстановить защиту
                // Ждёт пока файл освободится и применяет защиту заново
                bool fileProtected = false;
                for (int attempt = 0; attempt < 20; attempt++) // 10 секунд максимум
                {
                    try
                    {
                        // Применяет полную защиту заново
                        entry.Patches = ProtectionEngine.ApplyProtection(visiblePath);
                        fileProtected = true;
                        break;
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(500);
                    }
                }

                if (!fileProtected)
                {
                    MessageBox.Show(
                        "Не удалось заблокировать файл — он всё ещё используется.\n" +
                        "Закройте все копии программы и попробуйте заблокировать через Program Locker.exe.",
                        "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Пытается восстановить защиту с проверкой и повторами
                int maxRetries = 5;
                for (int retry = 0; retry < maxRetries; retry++)
                {
                    try
                    {
                        // Возвращает оригинал обратно в hiddenPath
                        if (File.Exists(hiddenPath))
                            File.Delete(hiddenPath);
                        File.Move(visiblePath, hiddenPath);

                        // Копирует лаунчер на место видимого файла
                        string launcherPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Locker Launcher.exe");
                        File.Copy(launcherPath, visiblePath, true);

                        // Заменяет иконку лаунчера на иконку оригинальной программы
                        IconReplacer.ReplaceIcon(hiddenPath, visiblePath);

                        // Финальная проверка — убеждается что лаунчер на месте и не был перезаписан
                        System.Threading.Thread.Sleep(500);

                        if (IsFileLauncher(visiblePath))
                        {
                            // Успех — лаунчер на месте
                            break;
                        }

                        // Лаунчер был перезаписан — вероятно продолжается обновление
                        if (retry < maxRetries - 1)
                        {
                            // Восстанавливает состояние для повторной попытки
                            if (File.Exists(visiblePath) && !IsFileLauncher(visiblePath))
                            {
                                // Новый файл появился — нужно его тоже защитить
                                if (File.Exists(hiddenPath))
                                {
                                    // Удаляет старый скрытый файл, он устарел
                                    File.Delete(hiddenPath);
                                }

                                // Ждёт стабилизации нового файла
                                System.Threading.Thread.Sleep(2000);

                                // Применяет защиту к новому файлу
                                entry.Patches = ProtectionEngine.ApplyProtection(visiblePath);
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // Файл занят — ждёт и повторяет
                        if (retry < maxRetries - 1)
                            System.Threading.Thread.Sleep(1000);
                    }
                }

                // Проверяет финальное состояние
                if (!IsFileLauncher(visiblePath))
                {
                    MessageBox.Show(
                        "Не удалось восстановить защиту — файл был изменён во время блокировки.\n" +
                        "Возможно, программа всё ещё обновляется.\n\n" +
                        "Попробуйте заблокировать вручную через Program Locker после завершения обновления.",
                        "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    // Пытаемся хотя бы сохранить текущие патчи
                    store.EncryptAndStoreEntries(entries, password);
                    SaveStore();
                    return;
                }

                store.EncryptAndStoreEntries(entries, password);
                SaveStore();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка: " + ex.Message);
            }
        }

        // WaitForAllProcessInstances ждёт завершения всех экземпляров процесса с указанным именем и путём
        private void WaitForAllProcessInstances(string exePath, string exeName)
        {
            int maxWaitSeconds = 3600; // Максимальное время ожидания 1 час
            int waited = 0;

            while (waited < maxWaitSeconds)
            {
                bool anyRunning = false;

                try
                {
                    var processes = Process.GetProcessesByName(exeName);

                    foreach (var p in processes)
                    {
                        try
                        {
                            // Пытается получить путь к exe процесса, чтобы убедиться, что это нужный нам процесс, а не одноименный
                            string processPath = null;
                            try
                            {
                                processPath = p.MainModule?.FileName;
                            }
                            catch
                            {
                                // Нет доступа к MainModule — считает, что это может быть наш процесс
                                anyRunning = true;
                            }

                            if (processPath != null &&
                                string.Equals(processPath, exePath, StringComparison.OrdinalIgnoreCase))
                            {
                                anyRunning = true;
                            }
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
                catch
                {
                    // Игнорирует ошибки при получении списка процессов и ждёт следующей попытки
                }

                if (!anyRunning)
                    break;

                System.Threading.Thread.Sleep(500);
                waited++;
            }
        }

        // WaitForStableState ждёт стабильного состояния: процесс не запущен и файл не меняется
        private bool WaitForStableState(string exePath, string exeName, DateTime originalModified, long originalSize, out bool fileChanged)
        {
            fileChanged = false;

            const int stabilizationChecks = 3;  // Количество проверок стабильности
            const int checkIntervalMs = 1000;   // Интервал между проверками (1 сек)
            const int maxTotalWaitMs = 20000;   // Максимальное общее ожидание (20 сек)

            int totalWaited = 0;
            int stableCount = 0;
            DateTime lastModified = originalModified;
            long lastSize = originalSize;

            while (totalWaited < maxTotalWaitMs)
            {
                System.Threading.Thread.Sleep(checkIntervalMs);
                totalWaited += checkIntervalMs;

                // Проверяет, не запущен ли процесс
                bool processRunning = IsProcessRunning(exePath, exeName);

                // Проверяет, не изменился ли файл
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
                        // Файл заблокирован — считаем нестабильным
                        stableCount = 0;
                        continue;
                    }
                }

                // Проверяет стабильность
                bool isStable = !processRunning &&
                                fileExists &&
                                currentModified == lastModified &&
                                currentSize == lastSize;

                if (isStable)
                {
                    stableCount++;
                    if (stableCount >= stabilizationChecks)
                    {
                        // Состояние стабильно — проверяем, изменился ли файл относительно оригинала
                        fileChanged = (currentModified != originalModified || currentSize != originalSize);
                        return true;
                    }
                }
                else
                {
                    // Сбрасывает счётчик стабильности
                    stableCount = 0;

                    // Если процесс запущен — ждёт его завершения
                    if (processRunning)
                    {
                        WaitForAllProcessInstances(exePath, exeName);
                    }
                }

                lastModified = currentModified;
                lastSize = currentSize;
            }

            // Таймаут
            return false;
        }

        // IsProcessRunning проверяет, запущен ли процесс с указанным именем и путём
        private bool IsProcessRunning(string exePath, string exeName)
        {
            try
            {
                var processes = Process.GetProcessesByName(exeName);

                foreach (var p in processes)
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
                            // Нет доступа — считает, что может быть наш
                            return true;
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

        // VerifyPassword проверяет мастер-пароль без выполнения разблокировки
        public bool VerifyPassword(string fakeExePath, string password)
        {
            try
            {
                var entries = store.DecryptEntries(password);
                string fullFake = Path.GetFullPath(fakeExePath);
                var entry = entries.FirstOrDefault(e => string.Equals(e.VisiblePath, fullFake, StringComparison.OrdinalIgnoreCase));
                return entry != null;
            }
            catch
            {
                return false;
            }
        }

        // BuildFullUI настраивает обработчики событий и обновляет начальное состояние интерфейса
        private void BuildFullUI()
        {
            // Инициализирует таймер для отложенной проверки пароля
            passwordCheckTimer = new System.Windows.Forms.Timer
            {
                Interval = 300 // 300мс после последнего ввода
            };
            passwordCheckTimer.Tick += PasswordCheckTimer_Tick;

            UpdateButtonStates();

            tbPass.TextChanged += TbPass_TextChanged;

            // Устанавливает обработчик нажатия клавиш на уровне формы
            this.KeyDown += Form_KeyDown;

            // Устанавливает обработчик закрытия формы
            this.FormClosing += ProgramForm_FormClosing;
        }

        // PasswordCheckTimer_Tick выполняет отложенную проверку пароля
        private void PasswordCheckTimer_Tick(object sender, EventArgs e)
        {
            passwordCheckTimer.Stop();

            string currentPass = tbPass.Text;

            // Проверяет пароль только если он изменился
            if (currentPass == cachedValidPassword && isPasswordValid)
            {
                return; // Уже проверен
            }

            // Сбрасывает кэш
            cachedValidPassword = null;
            isPasswordValid = false;

            if (!string.IsNullOrEmpty(currentPass) &&
                !string.IsNullOrEmpty(store.MasterHashBase64) &&
                store.VerifyMasterPassword(currentPass))
            {
                cachedValidPassword = currentPass;
                isPasswordValid = true;

                lblStatus.ForeColor = SystemColors.ControlText;
                RefreshFileList();
                lblNotes.Text = "Примечание:\n" +
                    "• Горячие клавиши: Ctrl+A — выделить всё, Delete — удалить выбранные, Enter — запустить выбранные.\n" +
                    "• Заблокированный файл заменяется лаунчером, при запуске будет запрошен пароль.\n" +
                    "• После закрытия программы защита автоматически восстанавливается.\n" +
                    "• Мастер-пароль один для всех программ.\n" +
                    "• Конфиг хранится тут: %AppData%\\Program Locker\\config.json";
                lblNotes.Visible = true;
            }
            else
            {
                lvFiles.Items.Clear();
                lblNotes.Text = "";
                lblNotes.Visible = false;

                if (string.IsNullOrEmpty(store.MasterHashBase64))
                {
                    lblStatus.Text = "Статус: Мастер-пароль не установлен";
                    lblStatus.ForeColor = Color.Red;
                }
                else if (string.IsNullOrEmpty(currentPass))
                {
                    lblStatus.Text = "Статус: Введите мастер-пароль";
                    lblStatus.ForeColor = Color.Red;
                }
                else
                {
                    lblStatus.Text = "Статус: —";
                    lblStatus.ForeColor = SystemColors.ControlText;
                }
            }

            UpdateButtonStates();
        }

        // TbPass_TextChanged запускает отложенную проверку пароля
        private void TbPass_TextChanged(object sender, EventArgs e)
        {
            // Сбрасывает кэш при изменении текста
            if (tbPass.Text != cachedValidPassword)
            {
                isPasswordValid = false;
            }

            // Перезапускает таймер
            passwordCheckTimer.Stop();
            passwordCheckTimer.Start();

            // Быстрое обновление кнопок (без проверки пароля)
            UpdateButtonStatesQuick();
        }

        // Form_KeyDown обрабатывает глобальные горячие клавиши
        private void Form_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                // Выделяет все элементы в списке
                if (lvFiles.Items.Count > 0)
                {
                    foreach (ListViewItem item in lvFiles.Items)
                        item.Selected = true;
                    lvFiles.Focus();
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                // Запускает удаление записи по клавише Delete
                if (lvFiles.SelectedItems.Count > 0 && btnRemoveEntry.Enabled)
                    BtnRemoveEntry_Click(btnRemoveEntry, EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                // Запускает выбранные программы по клавише Enter
                if (lvFiles.SelectedItems.Count > 0 && btnRun.Enabled)
                    BtnRun_Click(btnRun, EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // UpdateButtonStates обновляет состояние кнопок в зависимости от введенного пароля и выбора в списке
        private void UpdateButtonStates()
        {
            // Использует кэшированный результат проверки
            bool hasPassword = isPasswordValid && tbPass.Text == cachedValidPassword;
            bool hasSelection = lvFiles.SelectedItems.Count > 0;

            btnAddFile.Enabled = hasPassword;
            btnRun.Enabled = hasPassword && hasSelection;
            btnLock.Enabled = hasPassword && hasSelection;
            btnUnlock.Enabled = hasPassword && hasSelection;
            btnRemoveEntry.Enabled = hasPassword && hasSelection;
            btnRefresh.Enabled = hasPassword;

            if (!hasPassword && !string.IsNullOrEmpty(store.MasterHashBase64))
            {
                lblStatus.Text = "Статус: Введите мастер-пароль";
                lblStatus.ForeColor = Color.Red;
            }
        }

        // UpdateButtonStatesQuick быстро обновляет состояние кнопок (использует кэш)
        private void UpdateButtonStatesQuick()
        {
            bool hasPassword = isPasswordValid && tbPass.Text == cachedValidPassword;
            bool hasSelection = lvFiles.SelectedItems.Count > 0;

            btnAddFile.Enabled = hasPassword;
            btnRun.Enabled = hasPassword && hasSelection;
            btnLock.Enabled = hasPassword && hasSelection;
            btnUnlock.Enabled = hasPassword && hasSelection;
            btnRemoveEntry.Enabled = hasPassword && hasSelection;
            btnRefresh.Enabled = hasPassword;
        }

        // LvFiles_SelectedIndexChanged обновляет строку статуса при изменении выбора в списке
        private void LvFiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateButtonStates();

            if (lvFiles.SelectedItems.Count == 1)
            {
                string path = lvFiles.SelectedItems[0].Text;
                lblStatus.Text = $"Выбрано: {path}";
            }
            else if (lvFiles.SelectedItems.Count > 1)
            {
                lblStatus.Text = $"Выбрано файлов: {lvFiles.SelectedItems.Count}";
            }
            else
            {
                lblStatus.Text = "Статус: выберите файл(ы) из списка";
            }
        }

        // RefreshFileList обновляет список файлов, дешифруя данные из конфига
        private void RefreshFileList()
        {
            lvFiles.Items.Clear();

            if (string.IsNullOrEmpty(tbPass?.Text))
            {
                lblStatus.Text = "Статус: Введите пароль для отображения списка";
                return;
            }

            try
            {
                var entries = store.DecryptEntries(tbPass.Text);

                foreach (var entry in entries)
                {
                    string status = GetFileStatus(entry);
                    var item = new ListViewItem(entry.VisiblePath);
                    item.SubItems.Add(status);
                    item.SubItems.Add($"{entry.HiddenPath} ({entry.Patches?.Count ?? 0} патчей)"); // Показывает кол-во патчей
                    item.Tag = entry; // Сохраняет ссылку на запись, чтобы не искать её повторно

                    // Цветовая индикация
                    if (status.Contains("Защищён"))
                        item.BackColor = Color.LightGreen;
                    else if (status.Contains("Разблокирован"))
                        item.BackColor = Color.LightYellow;
                    else if (status.Contains("Ошибка") || status.Contains("Не найден"))
                        item.BackColor = Color.LightCoral;

                    lvFiles.Items.Add(item);
                }

                lblStatus.Text = $"Загружено записей: {entries.Count}";
            }
            catch (CryptographicException)
            {
                lblStatus.Text = "Статус: Неверный пароль";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Ошибка загрузки: " + ex.Message;
            }

            UpdateButtonStates();
        }

        // GetFileStatus определяет текущее состояние файла
        private string GetFileStatus(FileEntry entry)
        {
            bool visibleExists = File.Exists(entry.VisiblePath);
            bool hiddenExists = File.Exists(entry.HiddenPath);

            // Режим без лаунчера
            if (entry.NoLauncher)
            {
                if (!visibleExists && !hiddenExists)
                    return "Ошибка: файл не найден";

                // Файл в скрытом месте (_locked) = защищён
                if (hiddenExists && !visibleExists)
                {
                    if (IsFileProtected(entry, entry.HiddenPath))
                        return "Защищён (без лаунчера)";
                    else
                        return "Ошибка: файл не запатчен";
                }

                // Файл в видимом месте = разблокирован
                if (visibleExists && !hiddenExists)
                    return "Разблокирован (без лаунчера)";

                // Оба существуют = странное состояние
                return "Ошибка: дублирование файлов";
            }

            // Обычный режим с лаунчером
            if (!visibleExists && !hiddenExists)
                return "Ошибка: файлы не найдены";

            if (!visibleExists && hiddenExists)
                return "Ошибка: виден только скрытый";

            if (visibleExists && !hiddenExists)
            {
                if (IsFileLauncher(entry.VisiblePath))
                    return "Ошибка: скрытый не найден";
                else
                    return "Разблокирован";
            }

            // Оба файла существуют
            if (IsFileLauncher(entry.VisiblePath))
                return "Защищён";
            else
                return "Неизвестное состояние";
        }

        // IsFileProtected проверяет, защищён ли файл, сравнивая текущие байты с оригинальными
        private bool IsFileProtected(FileEntry entry, string filePath = null)
        {
            if (entry.Patches == null || entry.Patches.Count == 0)
                return false;

            string pathToCheck = filePath ?? entry.VisiblePath;

            if (!File.Exists(pathToCheck))
                return false;

            try
            {
                using var fs = new FileStream(pathToCheck, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // Проверяет первый патч (обычно AEP) — достаточно для определения состояния
                var firstPatch = entry.Patches[0];
                byte[] originalBytes = Convert.FromBase64String(firstPatch.OriginalBytesBase64);
                byte[] currentBytes = new byte[firstPatch.Length];

                fs.Seek(firstPatch.Offset, SeekOrigin.Begin);
                fs.Read(currentBytes, 0, firstPatch.Length);

                // Если текущие байты не совпадают с оригинальными — файл защищён
                for (int i = 0; i < originalBytes.Length; i++)
                {
                    if (originalBytes[i] != currentBytes[i])
                        return true; // Байты разные = защищён
                }

                return false; // Байты совпадают с оригиналом = разблокирован
            }
            catch
            {
                return true; // При ошибке считает защищённым
            }
        }

        // BtnAddFile_Click добавляет новый файл для защиты
        private void BtnAddFile_Click(object sender, EventArgs e)
        {
            if (!EnsurePasswordAvailable()) return;

            using var dlg = new OpenFileDialog()
            {
                Filter = "Executable (*.exe)|*.exe",
                CheckFileExists = true,
                Multiselect = true,
                Title = "Выберите файлы для защиты"
            };

            if (dlg.ShowDialog() != DialogResult.OK) return;

            // Показывает диалог выбора режима защиты
            HashSet<string> noLauncherFiles;
            using (var selectForm = new SelectNoLauncherForm(dlg.FileNames))
            {
                if (selectForm.ShowDialog() != DialogResult.OK) return;
                noLauncherFiles = selectForm.NoLauncherFiles;
            }

            string pass = tbPass.Text;
            var entries = store.DecryptEntries(pass);
            int added = 0, skipped = 0;

            foreach (string filePath in dlg.FileNames)
            {
                string fullPath = Path.GetFullPath(filePath);
                bool noLauncher = noLauncherFiles.Contains(fullPath);

                // Проверяет, не добавлен ли уже
                if (entries.Any(en => string.Equals(en.VisiblePath, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                // Проверяет, не является ли это лаунчером или "Program Locker"
                if (IsFileLauncher(filePath) ||
                    string.Equals(Path.GetFileName(filePath), "Program Locker.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(filePath), "Locker Launcher.exe", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"Файл {Path.GetFileName(filePath)} является частью Program Locker и не может быть защищён.",
                        "Пропуск", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    skipped++;
                    continue;
                }

                try
                {
                    // Применяет защиту
                    var patches = ProtectionEngine.ApplyProtection(filePath);

                    if (patches.Count == 0)
                    {
                        MessageBox.Show($"Не удалось определить AddressOfEntryPoint для {Path.GetFileName(filePath)}.", "Ошибка");
                        skipped++;
                        continue;
                    }

                    if (noLauncher)
                    {
                        // Режим БЕЗ лаунчера: переименовывает файл с суффиксом "_locked"
                        string dir = Path.GetDirectoryName(filePath);
                        string name = Path.GetFileNameWithoutExtension(filePath);
                        string ext = Path.GetExtension(filePath);
                        string hiddenPath = Path.Combine(dir, $"{name}_locked{ext}");
                        int i = 1;
                        while (File.Exists(hiddenPath)) hiddenPath = Path.Combine(dir, $"{name}_locked({i++}){ext}");

                        File.Move(filePath, hiddenPath);

                        entries.Add(new FileEntry
                        {
                            VisiblePath = fullPath,
                            HiddenPath = hiddenPath,
                            Patches = patches,
                            TimestampUtc = DateTime.UtcNow,
                            NoLauncher = true
                        });
                    }
                    else
                    {
                        // Обычный режим с лаунчером
                        string dir = Path.GetDirectoryName(filePath);
                        string name = Path.GetFileNameWithoutExtension(filePath);
                        string ext = Path.GetExtension(filePath);
                        string hiddenPath = Path.Combine(dir, $"{name}_locked{ext}");
                        int i = 1;
                        while (File.Exists(hiddenPath)) hiddenPath = Path.Combine(dir, $"{name}_locked({i++}){ext}");

                        File.Move(filePath, hiddenPath);

                        // Копирует лаунчер на место оригинала
                        string launcherPath = Path.Combine(Path.GetDirectoryName(CurrentExePath), "Locker Launcher.exe");
                        File.Copy(launcherPath, filePath, true);

                        // Заменяет иконку лаунчера
                        IconReplacer.ReplaceIcon(hiddenPath, filePath);

                        entries.Add(new FileEntry
                        {
                            VisiblePath = fullPath,
                            HiddenPath = hiddenPath,
                            Patches = patches,
                            TimestampUtc = DateTime.UtcNow,
                            NoLauncher = false
                        });
                    }

                    added++;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при добавлении {Path.GetFileName(filePath)}: {ex.Message}", "Ошибка");
                    skipped++;
                }
            }

            store.EncryptAndStoreEntries(entries, pass);
            SaveStore();
            RefreshFileList();

            MessageBox.Show($"Добавлено и защищено: {added}\nПропущено: {skipped}", "Результат",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // BtnRemoveEntry_Click удаляет запись из конфига и разблокирует связанные файлы, если они защищены
        private void BtnRemoveEntry_Click(object sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count == 0) return;
            if (!EnsurePasswordAvailable()) return;

            // Подсчитываем сколько файлов защищено
            int protectedCount = 0;
            foreach (ListViewItem item in lvFiles.SelectedItems)
            {
                if (item.Tag is FileEntry entry && GetFileStatus(entry) == "Защищён")
                    protectedCount++;
            }

            string message;
            if (protectedCount > 0)
            {
                message = $"Удалить {lvFiles.SelectedItems.Count} запись(ей)?\n\n" +
                          $"Защищённых файлов: {protectedCount}\n\n" +
                          "Все защищённые файлы будут АВТОМАТИЧЕСКИ РАЗБЛОКИРОВАНЫ\n" +
                          "перед удалением записи из конфига.\n\n" +
                          "Файлы вернутся в исходное рабочее состояние.";
            }
            else
            {
                message = $"Удалить {lvFiles.SelectedItems.Count} запись(ей)?\n\n" +
                          "Выбранные файлы не защищены или находятся в ошибочном состоянии.\n" +
                          "Записи будут удалены из конфига.";
            }

            var result = MessageBox.Show(message, "Подтверждение удаления",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            string pass = tbPass.Text;
            var entries = store.DecryptEntries(pass);
            int removed = 0, unlocked = 0, errors = 0;

            foreach (ListViewItem item in lvFiles.SelectedItems)
            {
                if (item.Tag is not FileEntry entry) continue;

                var toRemove = entries.FirstOrDefault(e =>
                    string.Equals(e.VisiblePath, entry.VisiblePath, StringComparison.OrdinalIgnoreCase));

                if (toRemove == null) continue;

                string status = GetFileStatus(toRemove);

                // Если файл защищён — сначала разблокирует
                if (status == "Защищён" || status == "Защищён (без лаунчера)")
                {
                    try
                    {
                        if (toRemove.NoLauncher)
                        {
                            // Режим без лаунчера: переименовывает и снимает патчи
                            if (File.Exists(toRemove.HiddenPath))
                            {
                                // Переименовывает в оригинальное имя
                                if (File.Exists(toRemove.VisiblePath))
                                    File.Delete(toRemove.VisiblePath);
                                File.Move(toRemove.HiddenPath, toRemove.VisiblePath);

                                // Снимает патчи
                                ProtectionEngine.RemoveProtection(toRemove.VisiblePath, toRemove.Patches);
                                unlocked++;
                            }
                            else if (File.Exists(toRemove.VisiblePath))
                            {
                                // Файл уже в оригинальном месте — просто снимает патчи
                                ProtectionEngine.RemoveProtection(toRemove.VisiblePath, toRemove.Patches);
                                unlocked++;
                            }
                        }
                        else if (File.Exists(toRemove.HiddenPath))
                        {
                            // Обычный режим
                            if (File.Exists(toRemove.VisiblePath))
                                File.Delete(toRemove.VisiblePath);

                            File.Move(toRemove.HiddenPath, toRemove.VisiblePath);
                            ProtectionEngine.RemoveProtection(toRemove.VisiblePath, toRemove.Patches);
                            unlocked++;
                        }
                        else
                        {
                            if (File.Exists(toRemove.VisiblePath) && !IsFileLauncher(toRemove.VisiblePath))
                            {
                                try
                                {
                                    ProtectionEngine.RemoveProtection(toRemove.VisiblePath, toRemove.Patches);
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка разблокировки {Path.GetFileName(toRemove.VisiblePath)}:\n{ex.Message}\n\n" +
                            "Запись всё равно будет удалена из конфига.",
                            "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        errors++;
                    }
                }
                // Если разблокирован — просто восстанавливает патчи, чтобы вернуть в рабочее состояние
                else if (status == "Разблокирован")
                {
                    try
                    {
                        // Восстанавливает все патчи
                        ProtectionEngine.RemoveProtection(toRemove.VisiblePath, toRemove.Patches);

                        unlocked++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка восстановления патчей для {Path.GetFileName(toRemove.VisiblePath)}:\n{ex.Message}",
                            "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        errors++;
                    }
                }

                // Удаляет запись из списка
                entries.Remove(toRemove);
                removed++;
            }

            store.EncryptAndStoreEntries(entries, pass);
            SaveStore();
            RefreshFileList();

            string resultMessage = $"Удалено записей: {removed}";
            if (unlocked > 0)
                resultMessage += $"\nРазблокировано файлов: {unlocked}";
            if (errors > 0)
                resultMessage += $"\nОшибок: {errors}";

            MessageBox.Show(resultMessage, "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // TryLoadStore пытается загрузить конфигурацию из файла или создаёт пустую, если файл не найден
        private void TryLoadStore()
        {
            try
            {
                Directory.CreateDirectory(AppFolder);
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    store = JsonConvert.DeserializeObject<ConfigStore>(json);
                    store ??= ConfigStore.CreateEmpty();
                }
                else
                {
                    store = ConfigStore.CreateEmpty();
                    SaveStore();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось загрузить конфиг: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                store = ConfigStore.CreateEmpty();
            }
        }

        // SaveStore сохраняет текущее состояние ConfigStore в файл "config.json"
        private void SaveStore()
        {
            // Всегда сохраняет актуальный путь к "Program Locker.exe", чтобы лаунчер мог его найти
            store.ProgLocExePath = CurrentExePath;

            var json = JsonConvert.SerializeObject(store, Formatting.Indented);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
        }

        // BtnSetPass_Click устанавливает или изменяет мастер-пароль
        private void BtnSetPass_Click(object sender, EventArgs e)
        {
            // Если пароль уже установлен — требует старый пароль для смены (для безопасности)
            if (!string.IsNullOrEmpty(store.MasterSaltBase64) && !string.IsNullOrEmpty(store.MasterHashBase64))
            {
                // Проверяет текущий пароль из основного поля
                string oldPass = tbPass.Text ?? "";

                if (string.IsNullOrEmpty(oldPass) || !store.VerifyMasterPassword(oldPass))
                {
                    MessageBox.Show("Сначала введите текущий пароль в основное поле.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tbPass.Focus();
                    return;
                }

                if (MessageBox.Show("Изменить мастер-пароль?",
                    "Подтвердите", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                // Запрашивает новый пароль
                using var newPassForm = new PasswordPromptForm("Введите новый пароль:");
                if (newPassForm.ShowDialog() != DialogResult.OK)
                    return;

                string newPass = newPassForm.Password;

                if (string.IsNullOrEmpty(newPass))
                {
                    MessageBox.Show("Новый пароль не может быть пустым.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Подтверждает новый пароль
                using var confirmPassForm = new PasswordPromptForm("Подтвердите новый пароль:");
                if (confirmPassForm.ShowDialog() != DialogResult.OK)
                    return;

                if (confirmPassForm.Password != newPass)
                {
                    MessageBox.Show("Пароли не совпадают. Попробуйте снова.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    // Дешифрует записи старым паролем, чтобы перешифровать их новым
                    var entries = store.DecryptEntries(oldPass);
                    store.SetupMaster(newPass);
                    store.EncryptAndStoreEntries(entries, newPass);
                    SaveStore();

                    tbPass.Clear();
                    cachedValidPassword = null;
                    isPasswordValid = false;
                    lvFiles.Items.Clear();
                    lblStatus.Text = "Статус: Введите новый пароль";
                    MessageBox.Show("Пароль изменён, записи перешифрованы.\nВведите новый пароль для продолжения работы.", "OK");
                    tbPass.Focus();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Ошибка при смене пароля: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                // Первая установка пароля
                string pass = tbPass.Text ?? "";
                if (string.IsNullOrEmpty(pass))
                {
                    MessageBox.Show("Введите непустой пароль.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Подтверждение нового пароля
                using var confirmPassForm = new PasswordPromptForm("Подтвердите новый пароль:");
                if (confirmPassForm.ShowDialog() != DialogResult.OK)
                    return;

                if (confirmPassForm.Password != pass)
                {
                    MessageBox.Show("Пароли не совпадают. Попробуйте снова.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                store.SetupMaster(pass);
                SaveStore();

                tbPass.Clear();
                cachedValidPassword = null;
                isPasswordValid = false;
                lvFiles.Items.Clear();
                lblStatus.Text = "Статус: Введите мастер-пароль";
                MessageBox.Show("Мастер-пароль установлен.\nВведите пароль для продолжения работы.", "OK");
                tbPass.Focus();
            }
        }

        // BtnLock_Click принудительно блокирует выбранные файлы
        private void BtnLock_Click(object sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count == 0)
            {
                MessageBox.Show("Выберите файл(ы) из списка или добавьте новые через кнопку 'Добавить файл'.", "Подсказка");
                return;
            }

            if (!EnsurePasswordAvailable()) return;
            string pass = tbPass.Text;
            var entries = store.DecryptEntries(pass);

            int locked = 0, skipped = 0, errors = 0;

            foreach (ListViewItem item in lvFiles.SelectedItems)
            {
                if (item.Tag is not FileEntry entry) continue;

                // Находит актуальную запись в entries (для обновления патчей)
                var actualEntry = entries.FirstOrDefault(e =>
                    string.Equals(e.VisiblePath, entry.VisiblePath, StringComparison.OrdinalIgnoreCase));

                if (actualEntry == null) continue;

                string status = GetFileStatus(actualEntry);

                // Уже защищён
                if (status == "Защищён" || status == "Защищён (без лаунчера)")
                {
                    skipped++;
                    continue;
                }

                // Разблокирован — нужно повторно заблокировать
                if (status == "Разблокирован" || status == "Разблокирован (без лаунчера)")
                {
                    try
                    {
                        if (actualEntry.NoLauncher)
                        {
                            // Режим без лаунчера: патчит и переименовывает
                            if (!File.Exists(actualEntry.VisiblePath))
                            {
                                MessageBox.Show($"Файл не найден: {actualEntry.VisiblePath}", "Ошибка");
                                errors++;
                                continue;
                            }

                            // Патчим файл
                            actualEntry.Patches = ProtectionEngine.ApplyProtection(actualEntry.VisiblePath);

                            // Переименовываем в _locked
                            if (File.Exists(actualEntry.HiddenPath))
                                File.Delete(actualEntry.HiddenPath);
                            File.Move(actualEntry.VisiblePath, actualEntry.HiddenPath);
                        }
                        else
                        {
                            // Обычный режим: патчит, перемещает, копирует лаунчер
                            actualEntry.Patches = ProtectionEngine.ApplyProtection(actualEntry.VisiblePath);

                            if (File.Exists(actualEntry.HiddenPath))
                                File.Delete(actualEntry.HiddenPath);
                            File.Move(actualEntry.VisiblePath, actualEntry.HiddenPath);

                            string launcherPath = Path.Combine(Path.GetDirectoryName(CurrentExePath), "Locker Launcher.exe");
                            File.Copy(launcherPath, actualEntry.VisiblePath, true);

                            IconReplacer.ReplaceIcon(actualEntry.HiddenPath, actualEntry.VisiblePath);
                        }

                        locked++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при блокировке {Path.GetFileName(actualEntry.VisiblePath)}: {ex.Message}", "Ошибка");
                        errors++;
                    }
                    continue;
                }

                // Другие статусы - пропускает с предупреждением
                MessageBox.Show($"Файл {Path.GetFileName(actualEntry.VisiblePath)} в состоянии '{status}' — пропущен.", "Пропуск");
                skipped++;
            }

            store.EncryptAndStoreEntries(entries, pass);
            SaveStore();
            RefreshFileList();

            if (locked > 0 || errors > 0)
                MessageBox.Show($"Заблокировано: {locked}\nПропущено: {skipped}\nОшибок: {errors}", "Результат");
        }

        // BtnUnlock_Click снимает защиту с выбранных файлов
        private void BtnUnlock_Click(object sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count == 0)
            {
                MessageBox.Show("Выберите файл(ы) из списка.", "Подсказка");
                return;
            }

            if (!EnsurePasswordAvailable()) return;
            string pass = tbPass.Text;
            var entries = store.DecryptEntries(pass);

            int unlocked = 0, skipped = 0, errors = 0;

            foreach (ListViewItem item in lvFiles.SelectedItems)
            {
                if (item.Tag is not FileEntry entry) continue;

                // Находит актуальную запись в entries (с актуальными патчами)
                var actualEntry = entries.FirstOrDefault(e =>
                    string.Equals(e.VisiblePath, entry.VisiblePath, StringComparison.OrdinalIgnoreCase));

                if (actualEntry == null)
                {
                    skipped++;
                    continue;
                }

                string status = GetFileStatus(actualEntry);

                // Уже разблокирован
                if (status == "Разблокирован" || status == "Разблокирован (без лаунчера)")
                {
                    skipped++;
                    continue;
                }

                // Ошибочные состояния
                if (status.Contains("Ошибка") || status.Contains("не найден"))
                {
                    MessageBox.Show(
                        $"Файл {Path.GetFileName(actualEntry.VisiblePath)} в состоянии '{status}'.\n\n" +
                        "Используйте кнопку 'Удалить' для удаления записи.",
                        "Пропуск", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    errors++;
                    continue;
                }

                // Нормальная разблокировка
                if (status == "Защищён" || status == "Защищён (без лаунчера)")
                {
                    try
                    {
                        if (actualEntry.NoLauncher)
                        {
                            // Режим без лаунчера: переименовывает и снимает патчи
                            if (!File.Exists(actualEntry.HiddenPath))
                            {
                                MessageBox.Show($"Защищённый файл не найден: {actualEntry.HiddenPath}", "Ошибка");
                                errors++;
                                continue;
                            }

                            // Переименовывает обратно в оригинальное имя
                            if (File.Exists(actualEntry.VisiblePath))
                                File.Delete(actualEntry.VisiblePath);
                            File.Move(actualEntry.HiddenPath, actualEntry.VisiblePath);

                            // Снимает патчи
                            ProtectionEngine.RemoveProtection(actualEntry.VisiblePath, actualEntry.Patches);
                        }
                        else
                        {
                            // Обычный режим
                            if (!File.Exists(actualEntry.HiddenPath))
                            {
                                MessageBox.Show($"Скрытый файл не найден: {actualEntry.HiddenPath}", "Ошибка");
                                errors++;
                                continue;
                            }

                            File.Delete(actualEntry.VisiblePath);
                            File.Move(actualEntry.HiddenPath, actualEntry.VisiblePath);
                            ProtectionEngine.RemoveProtection(actualEntry.VisiblePath, actualEntry.Patches);
                        }

                        // Снимает защиту
                        unlocked++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при разблокировке {Path.GetFileName(actualEntry.VisiblePath)}: {ex.Message}", "Ошибка");
                        errors++;
                    }
                    continue;
                }

                skipped++;
            }

            store.EncryptAndStoreEntries(entries, pass);
            SaveStore();
            RefreshFileList();

            MessageBox.Show($"Разблокировано: {unlocked}\nПропущено: {skipped}\nОшибок: {errors}", "Результат");
        }

        // BtnRun_Click запускает выбранные защищённые программы
        private void BtnRun_Click(object sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count == 0)
            {
                MessageBox.Show("Выберите файл(ы) из списка для запуска.", "Подсказка");
                return;
            }

            if (!EnsurePasswordAvailable()) return;

            string pass = tbPass.Text;
            var entries = store.DecryptEntries(pass);

            int launched = 0, skipped = 0, errors = 0;
            var launchedEntries = new List<(FileEntry entry, string visiblePath, string hiddenPath)>();

            foreach (ListViewItem item in lvFiles.SelectedItems)
            {
                if (item.Tag is not FileEntry entry) continue;

                var actualEntry = entries.FirstOrDefault(e =>
                    string.Equals(e.VisiblePath, entry.VisiblePath, StringComparison.OrdinalIgnoreCase));

                if (actualEntry == null) continue;

                string status = GetFileStatus(actualEntry);

                // Режим без лаунчера
                if (actualEntry.NoLauncher)
                {
                    // Если разблокирован — просто запускаем без снятия патчей
                    if (status == "Разблокирован (без лаунчера)")
                    {
                        try
                        {
                            Process.Start(actualEntry.VisiblePath);
                            launched++;
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Ошибка запуска {Path.GetFileName(actualEntry.VisiblePath)}:\n{ex.Message}", "Ошибка");
                            errors++;
                        }
                        continue;
                    }

                    // Если не защищён и не разблокирован — пропускаем
                    if (!status.Contains("Защищён"))
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        string visiblePath = actualEntry.VisiblePath;
                        string hiddenPath = actualEntry.HiddenPath;

                        if (!File.Exists(hiddenPath))
                        {
                            MessageBox.Show($"Защищённый файл не найден: {hiddenPath}", "Ошибка");
                            errors++;
                            continue;
                        }

                        // Переименовывает в оригинальное имя
                        if (File.Exists(visiblePath))
                            File.Delete(visiblePath);
                        File.Move(hiddenPath, visiblePath);

                        // Снимает патчи
                        ProtectionEngine.RemoveProtection(visiblePath, actualEntry.Patches);

                        // Запускает
                        Process.Start(visiblePath);

                        // Добавляет для мониторинга
                        launchedEntries.Add((actualEntry, visiblePath, hiddenPath));
                        launched++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка запуска {Path.GetFileName(actualEntry.VisiblePath)}:\n{ex.Message}", "Ошибка");
                        errors++;
                    }
                    continue;
                }

                // Обычный режим с лаунчером
                if (status != "Защищён")
                {
                    if (status == "Разблокирован")
                    {
                        try
                        {
                            Process.Start(actualEntry.VisiblePath);
                            launched++;
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Ошибка запуска {Path.GetFileName(actualEntry.VisiblePath)}:\n{ex.Message}", "Ошибка");
                            errors++;
                        }
                    }
                    else
                    {
                        skipped++;
                    }
                    continue;
                }

                try
                {
                    string visiblePath = actualEntry.VisiblePath;
                    string hiddenPath = actualEntry.HiddenPath;

                    if (!File.Exists(hiddenPath))
                    {
                        MessageBox.Show($"Скрытый файл не найден: {hiddenPath}", "Ошибка");
                        errors++;
                        continue;
                    }

                    File.Delete(visiblePath);
                    File.Move(hiddenPath, visiblePath);
                    ProtectionEngine.RemoveProtection(visiblePath, actualEntry.Patches);
                    Process.Start(visiblePath);

                    launchedEntries.Add((actualEntry, visiblePath, hiddenPath));
                    launched++;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка запуска {Path.GetFileName(actualEntry.VisiblePath)}:\n{ex.Message}", "Ошибка");
                    errors++;
                }
            }

            // Сохраняет текущее состояние
            store.EncryptAndStoreEntries(entries, pass);
            SaveStore();
            RefreshFileList();

            if (launched > 0)
            {
                string msg = $"Запущено программ: {launched}";
                if (skipped > 0) msg += $"\nПропущено: {skipped}";
                if (errors > 0) msg += $"\nОшибок: {errors}";

                lblStatus.Text = msg;
                lblStatus.ForeColor = SystemColors.ControlText;
            }

            // Запускает фоновое отслеживание для каждой программы
            if (launchedEntries.Count > 0)
            {
                foreach (var (_, visiblePath, hiddenPath) in launchedEntries)
                {
                    // Запускает отслеживание в отдельном потоке и сохраняет задачу
                    var task = System.Threading.Tasks.Task.Run(() =>
                        MonitorAndRelock(visiblePath, hiddenPath, pass));

                    lock (monitorTasksLock)
                    {
                        activeMonitorTasks.Add(task);
                    }
                }
            }
        }

        // MonitorAndRelock отслеживает процесс и восстанавливает защиту после его завершения
        private void MonitorAndRelock(string visiblePath, string hiddenPath, string password)
        {
            try
            {
                string exeName = Path.GetFileNameWithoutExtension(visiblePath);

                // Даёт время процессу запуститься
                System.Threading.Thread.Sleep(1000);

                // Ждёт завершения всех процессов
                WaitForAllProcessInstances(visiblePath, exeName);

                // Загружает актуальные entries
                List<FileEntry> entries;
                try
                {
                    entries = store.DecryptEntries(password);
                }
                catch
                {
                    return; // Не может расшифровать — выходит
                }

                var actualEntry = entries.FirstOrDefault(e =>
                    string.Equals(e.VisiblePath, visiblePath, StringComparison.OrdinalIgnoreCase));

                if (actualEntry == null) return;

                // Режим без лаунчера — патчит и переименовывает обратно
                if (actualEntry.NoLauncher)
                {
                    // Сохраняет информацию о файле для обнаружения изменений
                    DateTime originalModified = DateTime.MinValue;
                    long originalSize = 0;

                    if (File.Exists(visiblePath))
                    {
                        try
                        {
                            originalModified = File.GetLastWriteTimeUtc(visiblePath);
                            originalSize = new FileInfo(visiblePath).Length;
                        }
                        catch { }
                    }

                    // Ждёт стабильного состояния
                    if (!WaitForStableState(visiblePath, exeName, originalModified, originalSize, out _))
                        return;

                    if (!File.Exists(visiblePath))
                        return;

                    // Восстанавливает защиту
                    for (int retry = 0; retry < 5; retry++)
                    {
                        try
                        {
                            // Патчит файл
                            actualEntry.Patches = ProtectionEngine.ApplyProtection(visiblePath);

                            // Переименовывает в "_locked"
                            if (File.Exists(hiddenPath))
                                File.Delete(hiddenPath);
                            File.Move(visiblePath, hiddenPath);

                            // Сохраняет
                            store.EncryptAndStoreEntries(entries, password);
                            SaveStore();
                            break;
                        }
                        catch (IOException)
                        {
                            if (retry < 4) System.Threading.Thread.Sleep(1000);
                        }
                    }

                    UpdateUIAfterRelock();
                    return;
                }

                // Обычный режим с лаунчером
                // Сохраняет информацию о файле для обнаружения обновлений
                DateTime origModified = DateTime.MinValue;
                long origSize = 0;

                if (File.Exists(visiblePath))
                {
                    try
                    {
                        origModified = File.GetLastWriteTimeUtc(visiblePath);
                        origSize = new FileInfo(visiblePath).Length;
                    }
                    catch { }
                }

                // Период стабилизации
                if (!WaitForStableState(visiblePath, exeName, origModified, origSize, out bool fileChanged))
                {
                    return;
                }

                // Если файл не существует — ничего не делаем
                if (!File.Exists(visiblePath))
                {
                    return;
                }

                // Восстанавливает защиту
                int maxRetries = 5;
                for (int retry = 0; retry < maxRetries; retry++)
                {
                    try
                    {
                        // Применяет новые патчи
                        actualEntry.Patches = ProtectionEngine.ApplyProtection(visiblePath);

                        // Перемещает в скрытое место
                        if (File.Exists(hiddenPath))
                            File.Delete(hiddenPath);
                        File.Move(visiblePath, hiddenPath);

                        // Копирует лаунчер
                        string launcherPath = Path.Combine(
                            Path.GetDirectoryName(Application.ExecutablePath),
                            "Locker Launcher.exe");
                        File.Copy(launcherPath, visiblePath, true);

                        // Заменяет иконку
                        IconReplacer.ReplaceIcon(hiddenPath, visiblePath);

                        // Финальная проверка
                        System.Threading.Thread.Sleep(500);

                        if (IsFileLauncher(visiblePath))
                        {
                            // Успех — сохраняет конфиг
                            store.EncryptAndStoreEntries(entries, password);
                            SaveStore();
                            break;
                        }

                        // Лаунчер был перезаписан — повторная попытка
                        if (retry < maxRetries - 1)
                        {
                            if (File.Exists(visiblePath) && !IsFileLauncher(visiblePath))
                            {
                                if (File.Exists(hiddenPath))
                                    File.Delete(hiddenPath);

                                System.Threading.Thread.Sleep(2000);
                                actualEntry.Patches = ProtectionEngine.ApplyProtection(visiblePath);
                            }
                        }
                    }
                    catch (IOException)
                    {
                        if (retry < maxRetries - 1)
                            System.Threading.Thread.Sleep(1000);
                    }
                    catch
                    {
                        break;
                    }
                }

                UpdateUIAfterRelock();
            }
            catch
            {
                // Игнорирует ошибки в фоновом потоке
            }
            finally
            {
                // Удаляет текущую задачу из списка активных
                CleanupCompletedMonitorTasks();
            }
        }

        // UpdateUIAfterRelock обновляет UI после восстановления защиты (из фонового потока)
        private void UpdateUIAfterRelock()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() =>
                {
                    if (!string.IsNullOrEmpty(tbPass?.Text) && store.VerifyMasterPassword(tbPass.Text))
                    {
                        RefreshFileList();
                    }
                }));
            }
            else
            {
                if (!string.IsNullOrEmpty(tbPass?.Text) && store.VerifyMasterPassword(tbPass.Text))
                {
                    RefreshFileList();
                }
            }
        }

        // CleanupCompletedMonitorTasks удаляет завершённые задачи из списка активных
        private void CleanupCompletedMonitorTasks()
        {
            lock (monitorTasksLock)
            {
                activeMonitorTasks.RemoveAll(t => t.IsCompleted);
            }
        }

        // GetActiveMonitorCount возвращает количество активных задач мониторинга
        private int GetActiveMonitorCount()
        {
            lock (monitorTasksLock)
            {
                activeMonitorTasks.RemoveAll(t => t.IsCompleted);
                return activeMonitorTasks.Count;
            }
        }

        // WaitForMonitorsAndExit ждёт завершения всех мониторов и закрывает приложение
        private void WaitForMonitorsAndExit()
        {
            // Ждёт завершения всех мониторов
            while (GetActiveMonitorCount() > 0)
            {
                System.Threading.Thread.Sleep(1000);
            }

            // Закрывает приложение
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() =>
                {
                    forceClose = true;
                    Application.Exit();
                }));
            }
            else
            {
                forceClose = true;
                Application.Exit();
            }
        }

        // ShowTopMostMessageBox показывает MessageBox поверх всех окон
        private static void ShowTopMostMessageBox(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using var tempForm = new Form
            {
                TopMost = true,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(1, 1),
                Opacity = 0
            };
            tempForm.Show();
            MessageBox.Show(tempForm, text, caption, buttons, icon);
        }

        // ProgramForm_FormClosing обрабатывает закрытие формы
        private void ProgramForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Если принудительное закрытие — не мешаем
            if (forceClose)
                return;

            int activeCount = GetActiveMonitorCount();

            if (activeCount > 0)
            {
                var result = MessageBox.Show(
                    $"Есть запущенных программ под мониторингом: {activeCount}\n\n" +
                    "Закрыть Program Locker?\n" +
                    "Защита будет восстановлена в фоновом режиме после закрытия программ.",
                    "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    // Скрывает форму, но продолжает работать в фоне
                    this.Hide();
                    e.Cancel = true;

                    // Когда все мониторы завершатся — закрывает приложение
                    System.Threading.Tasks.Task.Run(() => WaitForMonitorsAndExit());
                }
                else
                {
                    e.Cancel = true;
                }
            }
        }

        // IsFileLauncher проверяет, является ли файл копией "Locker Launcher.exe" по метаданным версии
        private bool IsFileLauncher(string filePath)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(filePath);

                // Проверяет InternalName, ProductName или OriginalFilename
                if (versionInfo.InternalName != null &&
                    versionInfo.InternalName.IndexOf("Locker Launcher", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (versionInfo.ProductName != null &&
                    versionInfo.ProductName.IndexOf("Locker Launcher", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (versionInfo.OriginalFilename != null &&
                    versionInfo.OriginalFilename.IndexOf("Locker Launcher", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // EnsurePasswordAvailable проверяет наличие и корректность введенного мастер-пароля
        private bool EnsurePasswordAvailable()
        {
            if (string.IsNullOrEmpty(store.MasterHashBase64))
            {
                MessageBox.Show("Мастер-пароль не установлен.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (string.IsNullOrEmpty(tbPass?.Text))
            {
                MessageBox.Show("Введите мастер-пароль.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // Использует кэш если пароль не изменился
            if (isPasswordValid && tbPass.Text == cachedValidPassword)
            {
                return true;
            }

            // Проверяет пароль
            if (!store.VerifyMasterPassword(tbPass.Text))
            {
                MessageBox.Show("Неверный мастер-пароль.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // Кэширует результат
            cachedValidPassword = tbPass.Text;
            isPasswordValid = true;
            return true;
        }

        // Класс ConfigStore хранит всю конфигурацию приложения, включая зашифрованные записи
        class ConfigStore
        {
            public string MasterSaltBase64 { get; set; }
            public string MasterHashBase64 { get; set; }
            public string LastSelectedFile { get; set; }
            public string ProgLocExePath { get; set; } // Путь к "Program Locker.exe" для "Locker Launcher.exe"
            public int MasterIterations { get; set; } = 20_000;
            public EncryptedBlob EntriesBlob { get; set; }

            // CreateEmpty создаёт пустой экземпляр ConfigStore
            public static ConfigStore CreateEmpty() => new() { EntriesBlob = null };

            // SetupMaster генерирует соль и хеш для нового мастер-пароля
            public void SetupMaster(string password)
            {
                byte[] salt = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
                using var kdf = new Rfc2898DeriveBytes(password, salt, MasterIterations, HashAlgorithmName.SHA256);
                byte[] hash = kdf.GetBytes(32);
                MasterSaltBase64 = Convert.ToBase64String(salt);
                MasterHashBase64 = Convert.ToBase64String(hash);
            }

            // VerifyMasterPassword проверяет введенный пароль на соответствие мастер-хешу
            public bool VerifyMasterPassword(string password)
            {
                if (string.IsNullOrEmpty(MasterSaltBase64) || string.IsNullOrEmpty(MasterHashBase64)) return false;
                byte[] salt = Convert.FromBase64String(MasterSaltBase64);
                byte[] expected = Convert.FromBase64String(MasterHashBase64);
                using var kdf = new Rfc2898DeriveBytes(password, salt, MasterIterations, HashAlgorithmName.SHA256);
                byte[] got = kdf.GetBytes(32);
                return ConstantTimeEquals(got, expected);
            }

            // ClearEncryptedBlob удаляет зашифрованные данные
            public void ClearEncryptedBlob() => EntriesBlob = null;

            // DecryptEntries дешифрует список FileEntry с использованием мастер-пароля
            public List<FileEntry> DecryptEntries(string password) =>
                EntriesBlob == null ? [] : CryptoUtil.DecryptEntriesBlob(EntriesBlob, password);

            // EncryptAndStoreEntries шифрует и сохраняет список FileEntry
            public void EncryptAndStoreEntries(List<FileEntry> entries, string password) =>
                EntriesBlob = CryptoUtil.EncryptEntriesBlob(entries, password);

            // ConstantTimeEquals сравнивает два массива байтов за постоянное время, чтобы предотвратить атаки по времени
            static bool ConstantTimeEquals(byte[] a, byte[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                int diff = 0;
                for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
                return diff == 0;
            }
        }

        // Класс FileEntry представляет запись о заблокированном файле
        class FileEntry
        {
            public string VisiblePath { get; set; }             // Путь к фейковому EXE (тот, что пользователь видит и запускает)
            public string HiddenPath { get; set; }              // Путь к настоящему, оригинальному файлу (заблокированному)
            public DateTime TimestampUtc { get; set; }          // Дата и время добавления файла в защиту (UTC)
            public List<PatchInfo> Patches { get; set; } = [];  // Список всех патчей
            public bool NoLauncher { get; set; } = false;       // true = без лаунчера, запуск только через "Program Locker"
        }

        // Класс PatchInfo представляет информацию об одном патче
        class PatchInfo
        {
            public long Offset { get; set; }                // Смещение в файле
            public string OriginalBytesBase64 { get; set; } // Оригинальные байты
            public int Length { get; set; }                 // Длина патча
            public string Type { get; set; }                // Тип: "AEP", "EntryCode", "Random", "ImportDir"
        }

        // EncryptedBlob хранит данные, зашифрованные с помощью AES-256 (IV, соль, шифротекст, HMAC)
        class EncryptedBlob
        {
            public string SaltBase64 { get; set; }
            public string IvBase64 { get; set; }
            public string CiphertextBase64 { get; set; }
            public string HmacBase64 { get; set; }
            public int Iterations { get; set; }
        }

        // Класс CryptoUtil предоставляет статические методы для шифрования и дешифрования списка FileEntry
        static class CryptoUtil
        {
            // EncryptEntriesBlob шифрует список записей, используя AES-256 CBC
            public static EncryptedBlob EncryptEntriesBlob(List<FileEntry> entries, string password)
            {
                string plain = JsonConvert.SerializeObject(entries);
                byte[] plainBytes = Encoding.UTF8.GetBytes(plain);

                byte[] salt = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
                int iterations = 20_000;

                using var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
                byte[] key = kdf.GetBytes(32);
                byte[] hmacKey = kdf.GetBytes(32);

                using Aes aes = new AesCryptoServiceProvider();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.GenerateIV();
                byte[] iv = aes.IV;

                byte[] cipher;
                using (var ms = new MemoryStream())
                using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(plainBytes, 0, plainBytes.Length);
                    cs.FlushFinalBlock();
                    cipher = ms.ToArray();
                }

                byte[] toH = Concat(salt, iv, cipher);
                byte[] hmac;
                using (var h = new HMACSHA256(hmacKey)) hmac = h.ComputeHash(toH);

                return new EncryptedBlob
                {
                    SaltBase64 = Convert.ToBase64String(salt),
                    IvBase64 = Convert.ToBase64String(iv),
                    CiphertextBase64 = Convert.ToBase64String(cipher),
                    HmacBase64 = Convert.ToBase64String(hmac),
                    Iterations = iterations
                };
            }

            // DecryptEntriesBlob дешифрует список записей, проверяя HMAC для обеспечения целостности данных
            public static List<FileEntry> DecryptEntriesBlob(EncryptedBlob blob, string password)
            {
                if (blob == null) return [];

                byte[] salt = Convert.FromBase64String(blob.SaltBase64);
                byte[] iv = Convert.FromBase64String(blob.IvBase64);
                byte[] cipher = Convert.FromBase64String(blob.CiphertextBase64);
                byte[] hmacStored = Convert.FromBase64String(blob.HmacBase64);
                int iterations = blob.Iterations <= 0 ? 200_000 : blob.Iterations;

                using var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
                byte[] key = kdf.GetBytes(32);
                byte[] hmacKey = kdf.GetBytes(32);

                byte[] toH = Concat(salt, iv, cipher);
                using (var h = new HMACSHA256(hmacKey))
                {
                    byte[] computed = h.ComputeHash(toH);

                    // Проверяет HMAC, чтобы убедиться, что данные не были изменены и пароль верен
                    if (!ConstantTimeEquals(computed, hmacStored))
                        throw new CryptographicException("HMAC mismatch");
                }

                using Aes aes = new AesCryptoServiceProvider();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;

                byte[] plain;
                using (var ms = new MemoryStream())
                using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(cipher, 0, cipher.Length);
                    cs.FlushFinalBlock();
                    plain = ms.ToArray();
                }

                string json = Encoding.UTF8.GetString(plain);
                return JsonConvert.DeserializeObject<List<FileEntry>>(json) ?? [];
            }

            // Concat объединяет массивы байтов
            static byte[] Concat(params byte[][] parts)
            {
                int len = parts.Sum(p => p.Length);
                byte[] r = new byte[len];
                int pos = 0;
                foreach (var p in parts)
                {
                    Buffer.BlockCopy(p, 0, r, pos, p.Length);
                    pos += p.Length;
                }
                return r;
            }

            // ConstantTimeEquals сравнивает массивы байтов за постоянное время
            static bool ConstantTimeEquals(byte[] a, byte[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                int diff = 0;
                for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
                return diff == 0;
            }
        }

        // Класс PasswordPromptForm представляет простую форму для запроса пароля
        class PasswordPromptForm : Form
        {
            public string Password { get; private set; }    // Хранит введённый пароль

            private readonly TextBox tb;
            private readonly Button btnOk, btnCancel;

            // PasswordPromptForm инициализирует форму ввода пароля
            public PasswordPromptForm(string caption)
            {
                this.Text = "Ввод пароля";
                this.ClientSize = new Size(400, 130);
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.StartPosition = FormStartPosition.CenterParent;
                this.MaximizeBox = false;
                this.MinimizeBox = false;

                var lbl = new Label { Left = 15, Top = 15, Width = 370, Text = caption };
                tb = new TextBox { Left = 15, Top = 45, Width = 370, UseSystemPasswordChar = true };
                btnOk = new Button { Left = 220, Top = 80, Width = 80, Text = "OK", DialogResult = DialogResult.OK };
                btnCancel = new Button { Left = 310, Top = 80, Width = 80, Text = "Отмена", DialogResult = DialogResult.Cancel };

                btnOk.Click += (s, e) => { Password = tb.Text; };
                this.AcceptButton = btnOk;
                this.CancelButton = btnCancel;

                this.Controls.AddRange([lbl, tb, btnOk, btnCancel]);
            }
        }

        // Класс ProtectionEngine применяет и снимает усиленную защиту
        static class ProtectionEngine
        {
            // Быстрый генератор мусора
            private static readonly Random fastRng = new();
            private static readonly object rngLock = new();

            // Генерирует случайные "мусорные" байты
            private static byte[] GenerateGarbage(int length)
            {
                byte[] garbage = new byte[length];
                lock (rngLock)
                {
                    fastRng.NextBytes(garbage);
                }
                return garbage;
            }

            // Применяет защиту к файлу
            public static List<PatchInfo> ApplyProtection(string filePath)
            {
                var patches = new List<PatchInfo>();

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 65536); // Буфер для скорости
                using var br = new BinaryReader(fs);

                // Читает PE-заголовок
                if (fs.Length < 0x40)
                    throw new InvalidOperationException("Файл слишком маленький для PE");

                fs.Seek(0x3C, SeekOrigin.Begin);
                uint e_lfanew = br.ReadUInt32();

                if (e_lfanew + 0x78 > fs.Length)
                    throw new InvalidOperationException("Некорректный PE-заголовок");

                // Читает все нужные данные из PE-заголовка за один проход
                fs.Seek(e_lfanew + 4 + 2, SeekOrigin.Begin);
                ushort numberOfSections = br.ReadUInt16();

                fs.Seek(e_lfanew + 4 + 16, SeekOrigin.Begin);
                ushort sizeOfOptionalHeader = br.ReadUInt16();

                // Вычисляет offset AEP и читает его значение
                long aepOffset = e_lfanew + 4 + 20 + 16;
                fs.Seek(aepOffset, SeekOrigin.Begin);
                uint entryRva = br.ReadUInt32();

                // Вычисляет все смещения заранее (без записи)
                long entryFileOffset = RvaToFileOffset(fs, br, e_lfanew, entryRva);
                long importDirOffset = e_lfanew + 4 + 20 + 96 + 8;
                long checksumOffset = e_lfanew + 4 + 20 + 64;

                // Собирает все патчи для применения
                var patchesToApply = new List<(long offset, int length, string type)>
    {
        (aepOffset, 4, "AEP")
    };

                if (entryFileOffset > 0 && entryFileOffset + 16 <= fs.Length)
                    patchesToApply.Add((entryFileOffset, 16, "EntryCode"));

                if (importDirOffset + 8 <= fs.Length)
                    patchesToApply.Add((importDirOffset, 8, "ImportDir"));

                if (checksumOffset + 4 <= fs.Length)
                    patchesToApply.Add((checksumOffset, 4, "Checksum"));

                // Детерминированные "случайные" патчи в секции кода
                var codeSection = FindCodeSection(fs, br, e_lfanew);
                if (codeSection.HasValue)
                {
                    var (codeStart, codeSize) = codeSection.Value;

                    if (codeSize > 256)
                    {
                        int seed = unchecked(
                            (int)fs.Length ^
                            (int)e_lfanew ^
                            (int)numberOfSections ^
                            (int)codeStart ^
                            (int)codeSize
                        );

                        var deterministicRng = new Random(seed);
                        int patchCount = Math.Min(5, (int)(codeSize / 256));
                        var usedOffsets = new HashSet<long>();

                        for (int i = 0; i < patchCount; i++)
                        {
                            long randomOffset = codeStart + deterministicRng.Next(64, (int)codeSize - 64);
                            randomOffset = (randomOffset / 16) * 16;

                            bool overlaps = patchesToApply.Any(p =>
                                (randomOffset >= p.offset && randomOffset < p.offset + p.length) ||
                                (randomOffset + 16 > p.offset && randomOffset + 16 <= p.offset + p.length));

                            if (!usedOffsets.Contains(randomOffset) && !overlaps && randomOffset + 16 <= fs.Length)
                            {
                                usedOffsets.Add(randomOffset);
                                patchesToApply.Add((randomOffset, 16, $"Random_{i}"));
                            }
                        }
                    }
                }

                // Генерирует весь мусор заранее
                int totalGarbageSize = patchesToApply.Sum(p => p.length);
                byte[] allGarbage = new byte[totalGarbageSize];
                lock (rngLock)
                {
                    fastRng.NextBytes(allGarbage);
                }

                // Применяет все патчи за один проход
                int garbageOffset = 0;
                foreach (var (offset, length, type) in patchesToApply.OrderBy(p => p.offset))
                {
                    byte[] original = new byte[length];
                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.Read(original, 0, length);

                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.Write(allGarbage, garbageOffset, length);

                    patches.Add(new PatchInfo
                    {
                        Offset = offset,
                        Length = length,
                        OriginalBytesBase64 = Convert.ToBase64String(original),
                        Type = type
                    });

                    garbageOffset += length;
                }

                return patches;
            }

            // Патчит конкретное место и возвращает информацию о патче
            private static PatchInfo PatchLocation(FileStream fs, long offset, int length, string type)
            {
                byte[] original = new byte[length];

                fs.Seek(offset, SeekOrigin.Begin);
                fs.Read(original, 0, length);

                byte[] garbage = GenerateGarbage(length);

                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(garbage, 0, length);

                return new PatchInfo
                {
                    Offset = offset,
                    Length = length,
                    OriginalBytesBase64 = Convert.ToBase64String(original),
                    Type = type
                };
            }

            // Восстанавливает все патчи
            public static void RemoveProtection(string filePath, List<PatchInfo> patches)
            {
                if (patches == null || patches.Count == 0) return;

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 65536);

                // Сортирует патчи по смещению для последовательного доступа к диску
                foreach (var patch in patches.OrderBy(p => p.Offset))
                {
                    byte[] original = Convert.FromBase64String(patch.OriginalBytesBase64);
                    fs.Seek(patch.Offset, SeekOrigin.Begin);
                    fs.Write(original, 0, original.Length);
                }
            }
            
            // Преобразует RVA в файловое смещение
            private static long RvaToFileOffset(FileStream fs, BinaryReader br, uint e_lfanew, uint rva)
            {
                try
                {
                    // Читает количество секций
                    fs.Seek(e_lfanew + 4 + 2, SeekOrigin.Begin);
                    ushort numberOfSections = br.ReadUInt16();

                    // Размер Optional Header
                    fs.Seek(e_lfanew + 4 + 16, SeekOrigin.Begin);
                    ushort sizeOfOptionalHeader = br.ReadUInt16();

                    // Начало таблицы секций
                    long sectionTableOffset = e_lfanew + 4 + 20 + sizeOfOptionalHeader;

                    for (int i = 0; i < numberOfSections; i++)
                    {
                        long sectionOffset = sectionTableOffset + (i * 40);
                        fs.Seek(sectionOffset + 12, SeekOrigin.Begin); // VirtualAddress
                        uint virtualAddress = br.ReadUInt32();
                        uint sizeOfRawData = br.ReadUInt32();
                        uint pointerToRawData = br.ReadUInt32();

                        fs.Seek(sectionOffset + 8, SeekOrigin.Begin); // VirtualSize
                        uint virtualSize = br.ReadUInt32();

                        if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, sizeOfRawData))
                        {
                            return pointerToRawData + (rva - virtualAddress);
                        }
                    }
                }
                catch { }

                return -1;
            }

            // Находит секцию кода (.text)
            private static (long start, long size)? FindCodeSection(FileStream fs, BinaryReader br, uint e_lfanew)
            {
                try
                {
                    fs.Seek(e_lfanew + 4 + 2, SeekOrigin.Begin);
                    ushort numberOfSections = br.ReadUInt16();

                    fs.Seek(e_lfanew + 4 + 16, SeekOrigin.Begin);
                    ushort sizeOfOptionalHeader = br.ReadUInt16();

                    long sectionTableOffset = e_lfanew + 4 + 20 + sizeOfOptionalHeader;

                    for (int i = 0; i < numberOfSections; i++)
                    {
                        long sectionOffset = sectionTableOffset + (i * 40);

                        // Читает имя секции
                        fs.Seek(sectionOffset, SeekOrigin.Begin);
                        byte[] nameBytes = br.ReadBytes(8);
                        string name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

                        // Читает характеристики
                        fs.Seek(sectionOffset + 36, SeekOrigin.Begin);
                        uint characteristics = br.ReadUInt32();

                        // IMAGE_SCN_CNT_CODE = 0x00000020
                        // IMAGE_SCN_MEM_EXECUTE = 0x20000000
                        bool isCode = (characteristics & 0x20000020) != 0 ||
                                      name == ".text" || name == "CODE";

                        if (isCode)
                        {
                            fs.Seek(sectionOffset + 16, SeekOrigin.Begin); // SizeOfRawData
                            uint sizeOfRawData = br.ReadUInt32();
                            uint pointerToRawData = br.ReadUInt32();

                            if (sizeOfRawData > 128) // Минимум 128 байт
                            {
                                return (pointerToRawData, sizeOfRawData);
                            }
                        }
                    }
                }
                catch { }

                return null;
            }
        }


        // Класс IconReplacer заменяет иконку в исполняемом файле на иконку из защищаемого файла
        static class IconReplacer
        {
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool FreeLibrary(IntPtr hModule);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr LockResource(IntPtr hResData);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpType, EnumResNameProc lpEnumFunc, IntPtr lParam);

            private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, IntPtr lParam);

            private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
            private static readonly IntPtr RT_ICON = new(3);
            private static readonly IntPtr RT_GROUP_ICON = new(14);

            // ReplaceIcon копирует все иконки из sourceExe в targetExe с сохранением качества
            public static bool ReplaceIcon(string sourceExe, string targetExe)
            {
                IntPtr hSource = IntPtr.Zero;
                IntPtr hUpdate = IntPtr.Zero;

                try
                {
                    // Загружает исходный файл как данные (без выполнения)
                    hSource = LoadLibraryEx(sourceExe, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
                    if (hSource == IntPtr.Zero) return false;

                    // Собирает все ID иконок
                    var groupIconIds = new List<IntPtr>();
                    var iconIds = new List<IntPtr>();

                    EnumResourceNames(hSource, RT_GROUP_ICON, (hMod, lpType, lpName, lParam) =>
                    {
                        groupIconIds.Add(lpName);
                        return true;
                    }, IntPtr.Zero);

                    EnumResourceNames(hSource, RT_ICON, (hMod, lpType, lpName, lParam) =>
                    {
                        iconIds.Add(lpName);
                        return true;
                    }, IntPtr.Zero);

                    if (groupIconIds.Count == 0) return false;

                    // Сортирует группы иконок — главная иконка имеет наименьший числовой ID
                    groupIconIds.Sort((a, b) =>
                    {
                        long aVal = a.ToInt64();
                        long bVal = b.ToInt64();

                        // Числовые ID (< 65536) идут перед строковыми
                        bool aIsNumeric = aVal > 0 && aVal < 65536;
                        bool bIsNumeric = bVal > 0 && bVal < 65536;

                        if (aIsNumeric && !bIsNumeric) return -1;
                        if (!aIsNumeric && bIsNumeric) return 1;
                        if (aIsNumeric && bIsNumeric) return aVal.CompareTo(bVal);

                        return 0;
                    });

                    // Открывает целевой файл для обновления
                    hUpdate = BeginUpdateResource(targetExe, false);
                    if (hUpdate == IntPtr.Zero) return false;

                    // Сначала удаляет существующие иконки из целевого файла
                    // (передаём null данные с размером 0)
                    foreach (var id in GetExistingIconIds(targetExe))
                    {
                        UpdateResource(hUpdate, RT_ICON, id, 0, null, 0);
                    }
                    foreach (var id in GetExistingGroupIconIds(targetExe))
                    {
                        UpdateResource(hUpdate, RT_GROUP_ICON, id, 0, null, 0);
                    }

                    // Копирует все RT_GROUP_ICON из источника
                    foreach (var id in groupIconIds)
                    {
                        byte[] data = ExtractResource(hSource, RT_GROUP_ICON, id);
                        if (data != null)
                        {
                            // Используем стандартный ID 32512 для главной иконки
                            IntPtr targetId = (groupIconIds.IndexOf(id) == 0) ? new IntPtr(32512) : id;
                            UpdateResource(hUpdate, RT_GROUP_ICON, targetId, 0, data, (uint)data.Length);
                        }
                    }

                    // Копирует все RT_ICON из источника
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
                        EndUpdateResource(hUpdate, true);
                    return false;
                }
                finally
                {
                    if (hSource != IntPtr.Zero)
                        FreeLibrary(hSource);
                }
            }

            // ExtractResource извлекает данные ресурса по типу и ID
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
                    System.Runtime.InteropServices.Marshal.Copy(pData, data, 0, (int)size);
                    return data;
                }
                catch
                {
                    return null;
                }
            }

            // GetExistingIconIds возвращает список ID существующих RT_ICON в файле
            private static List<IntPtr> GetExistingIconIds(string exePath)
            {
                var ids = new List<IntPtr>();
                IntPtr hModule = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
                if (hModule == IntPtr.Zero) return ids;

                try
                {
                    EnumResourceNames(hModule, RT_ICON, (hMod, lpType, lpName, lParam) =>
                    {
                        ids.Add(lpName);
                        return true;
                    }, IntPtr.Zero);
                }
                finally
                {
                    FreeLibrary(hModule);
                }

                return ids;
            }

            // GetExistingGroupIconIds возвращает список ID существующих RT_GROUP_ICON в файле
            private static List<IntPtr> GetExistingGroupIconIds(string exePath)
            {
                var ids = new List<IntPtr>();
                IntPtr hModule = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
                if (hModule == IntPtr.Zero) return ids;

                try
                {
                    EnumResourceNames(hModule, RT_GROUP_ICON, (hMod, lpType, lpName, lParam) =>
                    {
                        ids.Add(lpName);
                        return true;
                    }, IntPtr.Zero);
                }
                finally
                {
                    FreeLibrary(hModule);
                }

                return ids;
            }
        }

        // Класс SelectNoLauncherForm создаёт форму выбора файлов без лаунчера
        class SelectNoLauncherForm : Form
        {
            public HashSet<string> NoLauncherFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

            private readonly CheckedListBox clbFiles;
            private readonly Button btnOk, btnCancel;
            private readonly Label lblInfo;

            public SelectNoLauncherForm(string[] filePaths)
            {
                this.Text = "Выбор режима защиты";
                this.ClientSize = new Size(420, 250);
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.StartPosition = FormStartPosition.CenterParent;
                this.MaximizeBox = false;
                this.MinimizeBox = false;

                lblInfo = new Label
                {
                    Left = 10,
                    Top = 8,
                    Width = 400,
                    Height = 50,
                    Text = "Снимите галочки с файлов, для которых НЕ нужен лаунчер.\n" +
                           "Такие файлы можно запустить ТОЛЬКО через Program Locker.\n" +
                           "(Использовать для браузеров: Chrome, Firefox..., из-за их системы защиты)."
                };

                clbFiles = new CheckedListBox
                {
                    Left = 10,
                    Top = 65,
                    Width = 400,
                    Height = 130,
                    CheckOnClick = true,
                    HorizontalScrollbar = true,
                    IntegralHeight = false // Позволяет точно задать высоту
                };

                // Добавляет файлы (по умолчанию все с галочками = с лаунчером)
                foreach (var path in filePaths)
                {
                    clbFiles.Items.Add(Path.GetFileName(path), true);
                }

                btnOk = new Button { Left = 250, Top = 210, Width = 75, Text = "OK", DialogResult = DialogResult.OK };
                btnCancel = new Button { Left = 335, Top = 210, Width = 75, Text = "Отмена", DialogResult = DialogResult.Cancel };

                btnOk.Click += (s, e) =>
                {
                    // Собирает файлы БЕЗ галочки (без лаунчера)
                    for (int i = 0; i < clbFiles.Items.Count; i++)
                    {
                        if (!clbFiles.GetItemChecked(i))
                        {
                            NoLauncherFiles.Add(filePaths[i]);
                        }
                    }
                };

                this.AcceptButton = btnOk;
                this.CancelButton = btnCancel;
                this.Controls.AddRange([lblInfo, clbFiles, btnOk, btnCancel]);
            }
        }

        // BtnRefresh_Click обновляет список файлов
        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            RefreshFileList();
        }

        // Ссылка на страницу автора
        private void Author_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://gitflic.ru/project/otto/program_locker");
        }
    }
}

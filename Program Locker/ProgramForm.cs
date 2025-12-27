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

                // Ждёт пока ВСЕ процессы этой программы завершатся, чтобы восстановить защиту
                string exeName = Path.GetFileNameWithoutExtension(visiblePath);
                System.Threading.Thread.Sleep(1000); // Даём время процессу запуститься и инициировать блокировку файла

                WaitForAllProcessInstances(visiblePath, exeName);

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

                // Возвращает оригинал обратно в hiddenPath
                File.Move(visiblePath, hiddenPath);

                // Копирует лаунчер на место видимого файла
                string launcherPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Locker Launcher.exe");
                File.Copy(launcherPath, visiblePath, true);
                
                // Заменяет иконку лаунчера на иконку оригинальной программы
                IconReplacer.ReplaceIcon(hiddenPath, visiblePath);

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
            UpdateButtonStates();

            tbPass.TextChanged += TbPass_TextChanged;

            // Устанавливает обработчик нажатия клавиш на уровне формы, чтобы ловить глобальные команды (Ctrl+A, Delete)
            this.KeyDown += Form_KeyDown;
        }

        // TbPass_TextChanged обновляет список файлов и статус, когда пользователь вводит пароль
        private void TbPass_TextChanged(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(tbPass.Text) &&
                !string.IsNullOrEmpty(store.MasterHashBase64) &&
                store.VerifyMasterPassword(tbPass.Text))
            {
                RefreshFileList();
                lblNotes.Text = "Примечание:\n" +
                    "• Горячие клавиши: Ctrl+A — выделить всё, Delete — удалить выбранные.\n" +
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
                    lblStatus.Text = "Статус: Мастер-пароль не установлен";
                else if (string.IsNullOrEmpty(tbPass.Text))
                    lblStatus.Text = "Статус: Введите мастер-пароль";
                else
                    lblStatus.Text = "Статус: —";
            }

            // Обновляет состояние кнопок, чтобы включить их, если пароль верен
            UpdateButtonStates();
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
        }

        // UpdateButtonStates обновляет состояние кнопок в зависимости от введенного пароля и выбора в списке
        private void UpdateButtonStates()
        {
            bool hasPassword = !string.IsNullOrEmpty(tbPass?.Text) && store.VerifyMasterPassword(tbPass.Text);
            bool hasSelection = lvFiles.SelectedItems.Count > 0;

            btnLock.Enabled = hasPassword && hasSelection;
            btnUnlock.Enabled = hasPassword && hasSelection;
            btnRemoveEntry.Enabled = hasPassword && hasSelection;
            btnRefresh.Enabled = hasPassword;
            btnAddFile.Enabled = hasPassword;

            if (!hasPassword && !string.IsNullOrEmpty(store.MasterHashBase64))
            {
                lblStatus.Text = "Статус: Введите мастер-пароль для работы со списком!";
            }
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

        // GetFileStatus определяет текущее состояние файла, сравнивая наличие видимого и скрытого файлов
        private string GetFileStatus(FileEntry entry)
        {
            bool visibleExists = File.Exists(entry.VisiblePath);
            bool hiddenExists = File.Exists(entry.HiddenPath);

            if (!visibleExists && !hiddenExists)
                return "Ошибка: файлы не найдены";

            if (!visibleExists && hiddenExists)
                return "Ошибка: виден только скрытый";

            if (visibleExists && !hiddenExists)
            {
                // Проверяем, лаунчер это или оригинал
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

            string pass = tbPass.Text;
            var entries = store.DecryptEntries(pass);
            int added = 0, skipped = 0;

            foreach (string filePath in dlg.FileNames)
            {
                string fullPath = Path.GetFullPath(filePath);

                // Проверяет, не добавлен ли уже, чтобы избежать дубликатов
                if (entries.Any(en => string.Equals(en.VisiblePath, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                // Проверяет, не является ли это лаунчером или "Program Locker.exe", потому что нельзя заблокировать саму программу
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

                    // Переименовывает оригинал, чтобы создать скрытый файл
                    string dir = Path.GetDirectoryName(filePath);
                    string name = Path.GetFileNameWithoutExtension(filePath);
                    string ext = Path.GetExtension(filePath);
                    string hiddenPath = Path.Combine(dir, $"{name}_locked{ext}");
                    int i = 1;
                    while (File.Exists(hiddenPath)) hiddenPath = Path.Combine(dir, $"{name}_locked({i++}){ext}");

                    File.Move(filePath, hiddenPath);

                    // Копирует лаунчер на место оригинала, чтобы защитить программу
                    string launcherPath = Path.Combine(Path.GetDirectoryName(CurrentExePath), "Locker Launcher.exe");
                    File.Copy(launcherPath, filePath, true);

                    // Заменяет иконку лаунчера на иконку оригинальной программы
                    IconReplacer.ReplaceIcon(hiddenPath, filePath);

                    // Добавляет запись
                    entries.Add(new FileEntry
                    {
                        VisiblePath = fullPath,
                        HiddenPath = hiddenPath,
                        Patches = patches,
                        TimestampUtc = DateTime.UtcNow
                    });

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
                if (status == "Защищён")
                {
                    try
                    {
                        if (File.Exists(toRemove.HiddenPath))
                        {
                            // Удаляет фейковый файл (лаунчер)
                            if (File.Exists(toRemove.VisiblePath))
                                File.Delete(toRemove.VisiblePath);

                            // Перемещает оригинал на место
                            File.Move(toRemove.HiddenPath, toRemove.VisiblePath);

                            // Восстанавливает все патчи
                            ProtectionEngine.RemoveProtection(toRemove.VisiblePath, toRemove.Patches);

                            unlocked++;
                        }
                        else
                        {
                            // Скрытый файл не найден — пробует восстановить патчи в видимом файле
                            if (File.Exists(toRemove.VisiblePath) && !IsFileLauncher(toRemove.VisiblePath))
                            {
                                try
                                {
                                    // Восстанавливает все патчи
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
                // Если разблокирован — просто восстанавливает AEP, чтобы вернуть его в рабочее состояние
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
                if (status == "Защищён")
                {
                    skipped++;
                    continue;
                }

                // Разблокирован — нужно повторно заблокировать
                if (status == "Разблокирован")
                {
                    try
                    {
                        // Применяет полную защиту заново, чтобы сделать оригинал нерабочим
                        actualEntry.Patches = ProtectionEngine.ApplyProtection(actualEntry.VisiblePath);

                        // Перемещает обратно в скрытое место
                        if (File.Exists(actualEntry.HiddenPath))
                            File.Delete(actualEntry.HiddenPath);
                        File.Move(actualEntry.VisiblePath, actualEntry.HiddenPath);

                        // Копирует лаунчер
                        string launcherPath = Path.Combine(Path.GetDirectoryName(CurrentExePath), "Locker Launcher.exe");
                        File.Copy(launcherPath, actualEntry.VisiblePath, true);

                        // Заменяет иконку лаунчера на иконку оригинальной программы
                        IconReplacer.ReplaceIcon(actualEntry.HiddenPath, actualEntry.VisiblePath);

                        locked++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при блокировке {Path.GetFileName(actualEntry.VisiblePath)}: {ex.Message}", "Ошибка");
                        errors++;
                    }
                    continue;
                }

                // Другие статусы — пропускает с предупреждением
                MessageBox.Show($"Файл {Path.GetFileName(actualEntry.VisiblePath)} в состоянии '{status}' — пропущен.", "Пропуск");
                skipped++;
            }

            store.EncryptAndStoreEntries(entries, pass);
            SaveStore();
            RefreshFileList();

            if (locked > 0 || errors > 0)
                MessageBox.Show($"Заблокировано: {locked}\nПропущено: {skipped}\nОшибок: {errors}", "Результат");
        }

        // BtnUnlock_Click выполняет полную разблокировку выбранных файлов и удаляет их из конфига
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

                string status = GetFileStatus(entry);

                // Уже разблокирован
                if (status == "Разблокирован")
                {
                    skipped++;
                    continue;
                }

                // Ошибочные состояния
                if (status.Contains("Ошибка") || status.Contains("не найден"))
                {
                    var result = MessageBox.Show(
                        $"Файл {Path.GetFileName(entry.VisiblePath)} в состоянии '{status}'.\n\n" +
                        "Удалить запись из конфига?",
                        "Проблема с файлом", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                    if (result == DialogResult.Yes)
                    {
                        entries.Remove(entry);
                    }
                    errors++;
                    continue;
                }

                // Нормальная разблокировка
                if (status == "Защищён")
                {
                    try
                    {
                        if (!File.Exists(entry.HiddenPath))
                        {
                            MessageBox.Show($"Скрытый файл не найден: {entry.HiddenPath}", "Ошибка");
                            errors++;
                            continue;
                        }

                        File.Delete(entry.VisiblePath);                 // Удаляет видимый лаунчер
                        File.Move(entry.HiddenPath, entry.VisiblePath); // Возвращает оригинал на место

                        // Восстанавливает все патчи
                        ProtectionEngine.RemoveProtection(entry.VisiblePath, entry.Patches);

                        // Удаляет запись из конфига (полная разблокировка)
                        entries.Remove(entry);
                        unlocked++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при разблокировке {Path.GetFileName(entry.VisiblePath)}: {ex.Message}", "Ошибка");
                        errors++;
                    }
                    continue;
                }

                skipped++;
            }

            store.EncryptAndStoreEntries(entries, pass);
            SaveStore();
            RefreshFileList();

            MessageBox.Show($"Разблокировано: {unlocked}\nПропущено: {skipped}\nОшибок/удалено записей: {errors}", "Результат");
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
            if (!store.VerifyMasterPassword(tbPass.Text))
            {
                MessageBox.Show("Неверный мастер-пароль.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
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
            private static readonly Random rng = new();

            // Генерирует случайные "мусорные" байты
            private static byte[] GenerateGarbage(int length)
            {
                byte[] garbage = new byte[length];
                using (var cryptoRng = RandomNumberGenerator.Create())
                {
                    cryptoRng.GetBytes(garbage);
                }
                return garbage;
            }

            // Применяет полную защиту к файлу
            public static List<PatchInfo> ApplyProtection(string filePath)
            {
                var patches = new List<PatchInfo>();

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var br = new BinaryReader(fs);

                // Читает PE-заголовок
                if (fs.Length < 0x40)
                    throw new InvalidOperationException("Файл слишком маленький для PE");

                fs.Seek(0x3C, SeekOrigin.Begin);
                uint e_lfanew = br.ReadUInt32();

                if (e_lfanew + 0x78 > fs.Length)
                    throw new InvalidOperationException("Некорректный PE-заголовок");

                // ПАТЧ 1: AddressOfEntryPoint (4 байта)
                long aepOffset = e_lfanew + 4 + 20 + 16;
                patches.Add(PatchLocation(fs, aepOffset, 4, "AEP"));

                // ПАТЧ 2: Первые 32 байта кода в точке входа
                fs.Seek(aepOffset, SeekOrigin.Begin);

                // Находит RVA и переводим в file offset
                fs.Seek(aepOffset, SeekOrigin.Begin);
                uint entryRva = br.ReadUInt32();
                long entryFileOffset = RvaToFileOffset(fs, br, e_lfanew, entryRva);

                if (entryFileOffset > 0 && entryFileOffset + 32 <= fs.Length)
                {
                    patches.Add(PatchLocation(fs, entryFileOffset, 32, "EntryCode"));
                }

                // ПАТЧ 3: Import Directory RVA (8 байт - RVA + Size)
                long importDirOffset = e_lfanew + 4 + 20 + 96 + 8; // Optional Header + Import Directory entry
                if (importDirOffset + 8 <= fs.Length)
                {
                    patches.Add(PatchLocation(fs, importDirOffset, 8, "ImportDir"));
                }

                // ПАТЧ 4-8: 5 случайных мест в секции кода
                var codeSection = FindCodeSection(fs, br, e_lfanew);
                if (codeSection.HasValue)
                {
                    var (codeStart, codeSize) = codeSection.Value;
                    int patchCount = Math.Min(5, (int)(codeSize / 256)); // Не больше 5 патчей

                    var usedOffsets = new HashSet<long>();

                    for (int i = 0; i < patchCount; i++)
                    {
                        // Случайное смещение внутри секции кода
                        long randomOffset = codeStart + rng.Next(64, (int)codeSize - 64);

                        // Выравнивает на 16 байт и проверяет уникальность
                        randomOffset = (randomOffset / 16) * 16;

                        if (!usedOffsets.Contains(randomOffset) &&
                            randomOffset + 16 <= fs.Length)
                        {
                            usedOffsets.Add(randomOffset);
                            patches.Add(PatchLocation(fs, randomOffset, 16, $"Random_{i}"));
                        }
                    }
                }

                // ПАТЧ 9: Checksum в PE-заголовке (4 байта)
                long checksumOffset = e_lfanew + 4 + 20 + 64;
                if (checksumOffset + 4 <= fs.Length)
                {
                    patches.Add(PatchLocation(fs, checksumOffset, 4, "Checksum"));
                }

                fs.Flush(true);
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

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                foreach (var patch in patches)
                {
                    byte[] original = Convert.FromBase64String(patch.OriginalBytesBase64);
                    fs.Seek(patch.Offset, SeekOrigin.Begin);
                    fs.Write(original, 0, original.Length);
                }

                fs.Flush(true);
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

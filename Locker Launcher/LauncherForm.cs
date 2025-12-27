// Copyright (c) 2025 Otto
// Лицензия: MIT (см. LICENSE)

using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Locker_Launcher
{
    public partial class LauncherForm : Form
    {
        private readonly string fakePath;   // Хранит путь к заблокированному файлу
        private string ProgLocExePath;      // Хранит путь к "Program Locker"

        // Путь к конфигу "%AppData%\Program Locker\config.json"
        static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Program Locker", "config.json");

        // LauncherForm инициализирует форму и сохраняет путь к блокируемому файлу
        public LauncherForm(string fakePath)
        {
            this.fakePath = fakePath;
            InitializeComponent();
            this.Load += LauncherForm_Load;
        }

        // LauncherForm_Load настраивает обработчики событий и пытается найти "Program Locker"
        private void LauncherForm_Load(object sender, EventArgs e)
        {
            // Сбрасывает сообщение об ошибке, когда пользователь начинает вводить текст
            Passwd.TextChanged += (s, ev) =>
            {
                if (ErrorInfo.Text.Length > 0)
                    ErrorInfo.Text = "";
            };

            btnOK.Click += BtnOk_Click;
            btnCancel.Click += (s, ev) => Application.Exit();

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;

            // Если путь не найден сразу, блокирует кнопку "ОК", чтобы предотвратить запуск без исполнительного файла
            if (!TryFindProgLocExe())
            {
                ErrorInfo.Text = "Не найден Program Locker!";
                btnOK.Enabled = false;
            }
        }

        // TryFindProgLocExe ищет "Program Locker" в стандартных расположениях или в конфигурационном файле
        private bool TryFindProgLocExe()
        {
            // Сначала пробует прочитать путь из общего конфигурационного файла
            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JObject.Parse(json);
                    string savedPath = config["ProgLocExePath"]?.ToString();

                    if (!string.IsNullOrEmpty(savedPath) && File.Exists(savedPath))
                    {
                        ProgLocExePath = savedPath;
                        return true;
                    }
                }
                catch
                {
                    // Игнорирует ошибки парсинга, чтобы перейти к поиску в стандартных местах
                }
            }

            //  Проверяет в папке рядом с этим файлом, чтобы найти его, если они лежат вместе
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Program Locker.exe");
            if (File.Exists(localPath))
            {
                ProgLocExePath = localPath;
                return true;
            }

            // Проверяет в папке конфига (если программа была перемещена)
            string configFolder = Path.GetDirectoryName(ConfigPath);
            string nearConfigPath = Path.Combine(configFolder, "Program Locker.exe");
            if (File.Exists(nearConfigPath))
            {
                ProgLocExePath = nearConfigPath;
                return true;
            }

            return false;
        }

        // BtnOk_Click обрабатывает нажатие кнопки "ОК", выполняет проверку пароля и запуск "Program Locker"
        private void BtnOk_Click(object sender, EventArgs e)
        {
            string pass = Passwd.Text;

            if (string.IsNullOrEmpty(pass))
            {
                ErrorInfo.Text = "Введите пароль";
                Passwd.Focus();
                return;
            }

            ErrorInfo.Text = "";

            // Повторная проверка на случай, если Program Locker появился после загрузки формы
            if (string.IsNullOrEmpty(ProgLocExePath) || !File.Exists(ProgLocExePath))
            {
                if (!TryFindProgLocExe())
                {
                    ErrorInfo.Text = "Не найден Program Locker";
                    return;
                }
            }

            // Блокирует интерфейс, пока идёт проверка пароля во внешнем процессе
            this.Enabled = false;
            this.Cursor = Cursors.WaitCursor;

            try
            {
                // Сначала проверяет пароль с использованием флага "--verify-only", не разблокируя файл
                var verifyInfo = new ProcessStartInfo
                {
                    FileName = ProgLocExePath,
                    Arguments = $"\"{fakePath}\" \"{pass}\" --verify-only",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                int exitCode;
                using (var verifyProgLoc = Process.Start(verifyInfo))
                {
                    verifyProgLoc.WaitForExit();
                    exitCode = verifyProgLoc.ExitCode;
                }

                if (exitCode != 0)
                {
                    // Пароль верный - показывает ошибку и даёт повторить
                    ErrorInfo.Text = "Неверный пароль";
                    Passwd.Focus();
                    Passwd.SelectAll();
                    return;
                }

                // Пароль верный — запускает основной процесс для разблокировки и запуска файла
                var startInfo = new ProcessStartInfo
                {
                    FileName = ProgLocExePath,
                    Arguments = $"\"{fakePath}\" \"{pass}\"",
                    UseShellExecute = true
                };

                Process.Start(startInfo);
                Application.Exit(); // Лаунчер завершает работу после успешного запуска
            }
            catch (Exception ex)
            {
                ErrorInfo.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                // Разблокирует интерфейс независимо от результата
                this.Enabled = true;
                this.Cursor = Cursors.Default;
            }
        }
    }
}

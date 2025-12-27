// Copyright (c) 2025 Otto
// Лицензия: MIT (см. LICENSE)

using System;
using System.Windows.Forms;

namespace Locker_Launcher
{
    internal static class Program
    {
        // Главная точка входа для приложения
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string targetFile = Application.ExecutablePath; // Путь к самому лаунчеру (фейковому EXE)

            var form = new LauncherForm(targetFile);
            Application.Run(form);
        }
    }
}

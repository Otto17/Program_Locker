// Copyright (c) 2025-2026 Otto
// Лицензия: MIT (см. LICENSE)

using System;
using System.Windows.Forms;

namespace Locker_Launcher
{
    internal static class Program
    {
        // Главная точка входа для приложения
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Если передан аргумент — использует его как путь к защищённому файлу (для запуска через ярлык с рабочего стола)
            string targetFile = (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
                ? args[0]
                : Application.ExecutablePath;

            var form = new LauncherForm(targetFile);
            Application.Run(form);
        }
    }
}

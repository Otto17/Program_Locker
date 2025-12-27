// Copyright (c) 2025 Otto
// Лицензия: MIT (см. LICENSE)

using System;
using System.Windows.Forms;

namespace Program_Locker
{
    internal static class Program
    {
        // Главная точка входа для приложения
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            ProgramForm form = new();

            // Режим проверки пароля (без разблокировки)
            if (args.Length >= 3 && args[2] == "--verify-only")
            {
                bool valid = form.VerifyPassword(args[0], args[1]);
                Environment.Exit(valid ? 0 : 1);
            }
            // Режим разблокировки и запуска
            else if (args.Length >= 2)
            {
                form.RunAsUnlocker(args[0], args[1]);
            }
            // Обычный режим с GUI
            else
            {
                Application.Run(form);
            }
        }
    }
}

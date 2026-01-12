// Copyright (c) 2025-2026 Otto
// Лицензия: MIT (см. LICENSE)

using System;
using System.ServiceProcess;

namespace Locker_Monitor
{
    internal static class Program
    {
        static void Main(string[] args)
        {
            // Обрабатывает аргументы командной строки
            if (args.Length > 0)
            {
                switch (args[0].ToLower())
                {
                    case "--add":
                        if (args.Length >= 5)
                        {
                            MonitorEngine.AddMonitorTask(args[1], args[2], args[3], args[4] == "true");
                            EnsureServiceRunning(); // Проверяет статус службы и пытается запустить её
                        }
                        return;

                    case "--remove":
                        if (args.Length >= 2)
                        {
                            MonitorEngine.RemoveMonitorTask(args[1]);
                        }
                        return;

                    case "--status":
                        MonitorEngine.ShowStatus();
                        return;
                }
            }

            // Запуск как служба Windows
            ServiceBase.Run(new LockerMonitorService());
        }

        // EnsureServiceRunning проверяет статус службы и запускает её при необходимости
        static void EnsureServiceRunning()
        {
            try
            {
                using var sc = new ServiceController("Locker Monitor"); // Использует ServiceController для взаимодействия со службой
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start(); // Запускает службу и ожидает статуса Running
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }
            }
            catch { }
        }
    }
}
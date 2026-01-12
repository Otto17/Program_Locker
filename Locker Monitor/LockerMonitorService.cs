// Copyright (c) 2025-2026 Otto
// Лицензия: MIT (см. LICENSE)

using System.ServiceProcess;

namespace Locker_Monitor
{
    // LockerMonitorService представляет службу мониторинга Locker
    public partial class LockerMonitorService : ServiceBase
    {
        // LockerMonitorService инициализирует параметры службы
        public LockerMonitorService()
        {
            ServiceName = "Locker Monitor";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        // OnStart запускает движок мониторинга
        protected override void OnStart(string[] args)
        {
            MonitorEngine.Start();
        }

        // OnStop останавливает движок мониторинга
        protected override void OnStop()
        {
            MonitorEngine.Stop();
        }
    }
}
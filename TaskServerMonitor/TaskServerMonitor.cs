using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace TaskServerMonitor
{
    public partial class TaskServerMonitor : ServiceBase
    {
        public TaskServerMonitor()
        {
            InitializeComponent();
            TSMonLog = new System.Diagnostics.EventLog();
            if (!System.Diagnostics.EventLog.SourceExists("DIALOG_TaskServerMonitor"))
            {
                System.Diagnostics.EventLog.CreateEventSource("DIALOG_TaskServerMonitor", "Application");
            }

            TSMonLog.Source = "DIALOG_TaskServerMonitor";
            TSMonLog.Log = "Application";
        }

        protected override void OnStart(string[] args)
        {
            TSMonLog.WriteEntry("DIALOG Task Server Monitor Starting...");
            TSMon_Service.Start();
        }

        protected override void OnStop()
        {
            TSMonLog.WriteEntry("DIALOG Task Server Monitor Stopping...");
            TSMon_Service.Stop();

        }
    }
}

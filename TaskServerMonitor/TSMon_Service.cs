using Newtonsoft.Json;
using NHttp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;



namespace TaskServerMonitor
{
    // State object for reading client data asynchronously  
    public class StateObject
    {
        // Client  socket.  
        public Socket workSocket = null;
        // Size of receive buffer.  
        public const int BufferSize = 1024;
        // Receive buffer.  
        public byte[] buffer = new byte[BufferSize];
        // Received data string.  
        public StringBuilder sb = new StringBuilder();
    }

    public class ServerStatus
    {
        public bool RevitIsRunning { get; set; }
        public bool ClarityTrayIsRunning { get; set; }
        public int FreeRAM { get; set; }
        public long SystemDriveFree { get; set; }
        public string ActiveTask { get; set; }
        public int ActiveTaskRuntime { get; set; }
        public string ServerState { get; set; }
    }

    /// <summary>
    /// This is the Status of ALL Accelerators
    /// </summary>
    public class Accelerators
    {
        public Dictionary<string, AcceleratorStatus> Accelerator { get; set; }
    }

    /// <summary>
    /// This is the name and status of ONE accelerator.  The bool array will have contain the status for the various Revit Server versions
    /// </summary>
    public class AcceleratorStatus
    {
        public string Name { get; set; }
        public Dictionary<string, bool> Status { get; set; }
    }

    class TSMon_Service
    {
        //the log object to use through out
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        //object to hold server current satus
        private static ServerStatus CurrentStatus = new ServerStatus();

        //object to hold the status of the accelerators
        private static Accelerators AcceleratorStatuses = new Accelerators();

        //this is used to lock the object for writing then needed
        private static object updateServerData = new object();


        //any internal variables needed for configuration
        internal static string default_server = ""; //this is the IP of the ???????? 
        internal static string default_server_port = "11000";
        internal static string log_file = "TSMon.log";
        internal static int timeout = 5000; //in msec
        internal static int collection_interval = 60000; //60 seconds
        internal static string taskserver_log = ""; //get this from the config file or ignore the update of this data
        internal static bool taskserver_log_available = false;
        internal static bool accelerator_check = false;


        //semafore event 
        public static ManualResetEvent allDone = new ManualResetEvent(false);

        public static void Start()
        {
            lock (updateServerData)
            {
                CurrentStatus.ServerState = "starting";
            }
#region Read Configuration
            //get the path of the executable
            string servicePath = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().CodeBase);

            //read the config file (hopefully)

            try
            {
                XElement serverConfig = XElement.Load(servicePath + "\\tsmon.config");
                default_server_port = serverConfig.Element("port").Value;
                timeout = int.Parse(serverConfig.Element("timeout_msec").Value);
                collection_interval = int.Parse(serverConfig.Element("refresh").Value);
                try
                {
                    taskserver_log = serverConfig.Element("clarity_log").Value;
                    taskserver_log_available = true;
                }
                catch (Exception)
                {
                    //never mind; task server log not available to monitor
                }


                //determine if i'm responsible for checking the accelerators
                try
                {
                    accelerator_check = Boolean.Parse(serverConfig.Element("accelerator_check").Value);

                    //if I am then read in the list of accelerators to check
                    if (accelerator_check)
                    {
                        XElement accConfig = serverConfig.Element("accelerators");
                        XElement accCommands = serverConfig.Element("accelerator_wsdl");
                    }
                }
                catch (Exception)
                { }

                #endregion

#region Setup Networking
                //setup the network
                IPHostEntry ipHostInfo;
                IPAddress ipAddress;
                IPEndPoint localEndPoint;

                //start a lightweight HTTP server to handle requests
                using (var httpd = new HttpServer())
                {
                    //first try with the IP address given (or name or whatever)
                    int server_port = 0;
                    //find the address and port to run the server at

                    try
                    {
                        if (default_server == "")
                        {
                            throw new Exception();
                        }

                        ipHostInfo = Dns.GetHostEntry(default_server);
                        ipAddress = Array.Find(ipHostInfo.AddressList, a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (!int.TryParse(default_server_port, out server_port))
                        {
                            log.Error("Server port configuration is incorrect, make sure the port is specified in the server_config.xml file");
                            throw new Exception("Server port configuration is incorrect, make sure the port is specified in the server_config.xml file");
                        }

                        httpd.EndPoint = new IPEndPoint(ipAddress, server_port);
                    }
                    //if that fails try to figure it out on our own
                    catch (Exception)
                    {
                        log.Info("Failed to use address provided " + default_server + ". Trying to find my own way...");
                        ipHostInfo = Dns.GetHostEntry(Dns.GetHostName());
                        ipAddress = Array.Find(ipHostInfo.AddressList, a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (!int.TryParse(default_server_port, out server_port))
                        {
                            log.Error("Server port configuration is incorrect, make sure the port is specified in the server_config.xml file");
                            throw new Exception("Server port configuration is incorrect, make sure the port is specified in the server_config.xml file");
                        }

                        httpd.EndPoint = new IPEndPoint(IPAddress.Any, server_port);
                    }
                    #endregion

#region Command Processors

                    httpd.RequestReceived += (s, e) =>
                    {
                        string rq = e.Request.Path;
                        e.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                        e.Response.Headers.Add("Access-Control-Allow-Methods", "POST, GET");
                        switch (rq)
                        {
                            case "/status/":
                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    
                                    res.Write(JsonConvert.SerializeObject(CurrentStatus));
                                }
                                break;
                            case "/statusupdate/":                                           //someone is impatient
                                OnTimerDataCollection(null, null);
                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    res.Write(JsonConvert.SerializeObject(CurrentStatus));
                                }
                                break;
                            case "/cleanup/":
                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    res.Write("Don't know how to cleanup yet.", rq);
                                }
                                break;
                            case "/deepclean/":
                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    res.Write("Don't know how to do deep clean yet.", rq);
                                }
                                break;
                            case "/reboot/":

                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    res.Write("Don't know how to reboot yet.", rq);
                                }
                                break;
                            case "/pause/":
                                PauseTaskServer();
                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    res.Write(JsonConvert.SerializeObject(CurrentStatus));
                                }
                                break;
                            case "/resume/":
                                UnPauseTaskServer();
                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    res.Write(JsonConvert.SerializeObject(CurrentStatus));
                                }
                                break;
                            case "/accelerators/":
                                CheckAccelerators();
                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    res.Write(JsonConvert.SerializeObject(AcceleratorStatuses));
                                }
                                break;
                            default:
                                using (var res = new StreamWriter(e.Response.OutputStream))
                                {
                                    res.Write("I'm afraid I don't know what to do for \"{0}\"", rq);
                                }
                                break;
                        }
                    };

                    httpd.Start();
                    #endregion


                    lock (updateServerData)
                    {
                        CurrentStatus.ServerState = "idle";
                    }

#if DEBUG
                    //Process.Start(String.Format("http://{0}/", httpd.EndPoint));
#endif

                    //DESIGN: We don't need "real-time" data, as data collection is expensive lets collect data every <collection_interval> and provide it to 
                    //        whoever asks for it

                    //get the current status
                    OnTimerDataCollection(null, null);

                    //now start a timer that will collect data and return it to whom ever asks for status.
                    System.Timers.Timer tmrDataCollector = new System.Timers.Timer();
                    tmrDataCollector.Interval = collection_interval;
                    tmrDataCollector.Elapsed += new System.Timers.ElapsedEventHandler(OnTimerDataCollection);
                    tmrDataCollector.Start();

#if DEBUG
                    Console.WriteLine("Task Server Monitor running on port " + server_port + "\nPress any key to QUIT...");
                    Console.ReadKey();
#endif
                }

            }
            catch (Exception m)
            {
                Debug.WriteLine(m.Message);
                throw;
            }
        }



        public static void Stop()
        {

        }



        /// <summary>
        /// This method will do the actual data collection from the server and store it in an internal object that can be sent to 
        /// any one who requests the data.  It may also be logged here
        /// 
        /// The loggin will be implemented later
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// 
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);
        public static void OnTimerDataCollection(object sender, System.Timers.ElapsedEventArgs args)
        {


            //is Revit Running
            Process[] psRevit = Process.GetProcessesByName("Revit");
            bool bRevitIsRunning = (psRevit.Count() > 0);
            
            //is the Clarity Tray Process Running
            Process[] psClarityTray = Process.GetProcessesByName("ClarityTaskTray");
            bool bClarityTaskTray = (psClarityTray.Count() > 0);

            //get available RAM
            var ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            float iAvailableRAM = ramCounter.NextValue();

            //and available disk space
            bool success = GetDiskFreeSpaceEx(@"C:\\",
                                                out ulong FreeBytesAvailable,
                                                out ulong TotalNumberOfBytes,
                                                out ulong TotalNumberOfFreeBytes);

            //see if we can tell what task is running
            string currentTask = "unknown";
            int taskRuntime = 0;

            try
            {
                if (bRevitIsRunning && taskserver_log_available)
                {
                    string[] logFile;

                    using (var fs = new FileStream(taskserver_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.Default))
                    {
                        logFile = sr.ReadToEnd().Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
                    }

                        foreach (string line in logFile)
                        {
                            if (line.Contains("Executing Task: Task:"))
                            {
                                currentTask = line.Substring(77, 6);
                                DateTime taskStarted = Convert.ToDateTime(line.Substring(6, 19));
                                taskRuntime = (int)DateTime.Now.Subtract(taskStarted).TotalMinutes;
                            }
                        }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Debug.WriteLine("Can't read from file, are you sure its there?");
            }


            //check accelerators if its our job
            if (accelerator_check)
            {

            }

            //update the data object for future requests to feed to the clients
            lock (updateServerData)
            {
                CurrentStatus.RevitIsRunning = bRevitIsRunning;

                if (CurrentStatus.ServerState == "pausing" && !bRevitIsRunning)
                {
                    CurrentStatus.ServerState = "paused";
                }
                else
                {
                    if (bRevitIsRunning && CurrentStatus.ServerState != "pausing")
                    {
                        CurrentStatus.ServerState = "working";
                    }
                    else
                    {
                        if (!bRevitIsRunning)
                        {
                            CurrentStatus.ServerState = "idle";
                        }
                    }
                }

                CurrentStatus.ClarityTrayIsRunning = bClarityTaskTray;
                if (!CurrentStatus.ClarityTrayIsRunning)
                {
                    CurrentStatus.ServerState = "nouser";
                }
                CurrentStatus.FreeRAM = (int)iAvailableRAM;
                CurrentStatus.SystemDriveFree = (long)FreeBytesAvailable;
                CurrentStatus.ActiveTask = currentTask;
                CurrentStatus.ActiveTaskRuntime = taskRuntime;
            }
        }


        /// <summary>
        /// This will request to PAUSE the task server.  The request will be sent to the clarity host, NO TASKS WILL BE QUIT
        /// </summary>
        public static void PauseTaskServer()
        {
            //request Clarity Host to Pause this server
            //
            //somehow

            lock (updateServerData)
            {
                CurrentStatus.ServerState = "pausing";
            }
        }


        private static void UnPauseTaskServer()
        {
            //request Clarity Host to RESUME this server
            //
            //somehow

            OnTimerDataCollection(null, null);
        }


        /// <summary>
        /// This function checks through all the accelerators and gets their statuses
        /// </summary>
        private static void CheckAccelerators()
        {
            foreach (string acceleratorName in AcceleratorStatuses.Accelerator.Keys)
            {
                AcceleratorStatus accelerator = AcceleratorStatuses.Accelerator[acceleratorName];
                foreach (string revitServer in accelerator.Status.Keys)
                {
                    accelerator.Status[revitServer] = CheckServerAtAccelerator(acceleratorName, revitServer);
                }
            }
        }


        /// <summary>
        /// Here we will reach out to the accelerator at <paramref name="acceleratorName"/> and ask it for the status of the <paramref name="revitServer"/>
        /// </summary>
        /// <param name="acceleratorName"></param>
        /// <param name="revitServer"></param>
        /// <returns>The status of the accelerator as returned from the web service call</returns>
        private static bool CheckServerAtAccelerator(string acceleratorName, string revitServer)
        {
            return false;
        }
    }
}

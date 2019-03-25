using Newtonsoft.Json;
using System;
using System.Diagnostics;
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
    }

    class TSMon_Service
    {
        //the log object to use through out
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        //object to hold server current satus
        private static ServerStatus CurrentStatus = new ServerStatus();

        //this is used to lock the object for writing then needed
        private static object updateServerData = new object();

        //any internal variables needed for configuration
        internal static string default_server = ""; //this is the IP of the ???????? 
        internal static string default_server_port = "11000";
        internal static string log_file = "TSMon.log";
        internal static int timeout = 5000; //in msec
        internal static int collection_interval = 60000; //60 seconds


        //the maximum number of characters we are willing to read form the client.
        static readonly int maxMessageSize = 256;

        //our listening socket
        internal static Socket listener;

        //semafore event 
        public static ManualResetEvent allDone = new ManualResetEvent(false);

        public static void Start()
        {
            //get the path of the executable
            string servicePath = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().CodeBase);

            //read the config file (hopefully)

            try
            {
                //XElement serverConfig = XElement.Load(servicePath + "\\tsmon.config");
                //default_server_port = serverConfig.Element("port").Value;
                //timeout = int.Parse(serverConfig.Element("timeout_msec").Value);
                //collection_interval = int.Parse(serverConfig.Element("collection_interval").Value);

                //create and start the listener thread
                Thread loop = new Thread(StartListenning);
                loop.Start();

                //DESIGN: We don't need "real-time" data, as data collection is expensive lets collect data every <collection_interval> and provide it to 
                //        whoever asks for it


                //get the current status
                OnTimerDataCollection(null, null);

                //now start a timer that will collect data and return it to whom ever asks for status.
                System.Timers.Timer tmrDataCollector = new System.Timers.Timer();
                tmrDataCollector.Interval = collection_interval;
                tmrDataCollector.Elapsed += new System.Timers.ElapsedEventHandler(OnTimerDataCollection);
                tmrDataCollector.Start();



            }
            catch (Exception m)
            {
                Console.WriteLine(m.Message);
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

            var ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            float iAvailableRAM = ramCounter.NextValue();

            ulong FreeBytesAvailable;
            ulong TotalNumberOfBytes;
            ulong TotalNumberOfFreeBytes;

            bool success = GetDiskFreeSpaceEx(@"C:\\",
                                                out FreeBytesAvailable,
                                                out TotalNumberOfBytes,
                                                out TotalNumberOfFreeBytes);

            lock (updateServerData)
            {
                CurrentStatus.RevitIsRunning = bRevitIsRunning;
                CurrentStatus.ClarityTrayIsRunning = bClarityTaskTray;
                CurrentStatus.FreeRAM = (int)iAvailableRAM;
                CurrentStatus.SystemDriveFree = (long)FreeBytesAvailable;
            }
        }

        /// <summary>
        /// This is the listening loop thread, it is responsible for setting up the socket and starting the listen loop
        /// </summary>
        private static void StartListenning()
        {
            int server_port = 0;

            //create the async logger

            //setup the network
            IPHostEntry ipHostInfo;
            IPAddress ipAddress;
            IPEndPoint localEndPoint;


            //first try with the IP address given (or name or whatever)
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

                localEndPoint = new IPEndPoint(ipAddress, server_port);
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

                localEndPoint = new IPEndPoint(IPAddress.Any, server_port);
            }
            //IPHostEntry ipHostInfo = Dns.GetHostEntry(Dns.GetHostName());

            //Create a TCP/IP socket

            listener = new Socket(ipAddress.AddressFamily,
                        SocketType.Stream, ProtocolType.Tcp);

            Console.WriteLine("Waiting for connection on {0}:{1}...", localEndPoint.Address.ToString(), localEndPoint.Port);
            log.Info("Starting Server on " + localEndPoint.Address.ToString() + ":" + localEndPoint.Port);
            // bind and listen
            try
            {
                listener.Bind(localEndPoint);
                listener.ReceiveTimeout = timeout;
                listener.Listen(50);

                while (true)
                {
                    //reset signal
                    allDone.Reset();

                    //start an async socker and listen for connections

                    listener.BeginAccept(new AsyncCallback(AcceptCallback), listener);
                    allDone.WaitOne();
                }
            }
            catch (Exception e)
            {
                log.Fatal(e.ToString());
                Console.WriteLine(e.ToString());
            }
        }

        public static void AcceptCallback(IAsyncResult ar)
        {
            //Singnal the main thread to resume/continue
            allDone.Set();

            Socket listener = (Socket)ar.AsyncState;
            Socket handler = listener.EndAccept(ar);
            Console.WriteLine("Got connection from {0}, resuming listening mode...", IPAddress.Parse(((IPEndPoint)handler.RemoteEndPoint).Address.ToString()));
            log.Info(String.Format("Got Connection from {0}, going back to lisening mode...", IPAddress.Parse(((IPEndPoint)handler.RemoteEndPoint).Address.ToString())));

            char[] charsToTrim = { '\n', '\r' };

            //setup is done - start reading and work

            //handler.Send(Encoding.ASCII.GetBytes("OK"));

            //we will wait at most <timeout> seconds for the whole transaction from start to end
            //anything that takes longer than <timeout> we are just gonna drop.
            var killConnectionTimer = new Timer(KillConnection, handler, timeout, 0);

            //Create state object
            StateObject s = new StateObject();
            s.workSocket = handler;

            byte[] bytes = new Byte[1024];
            String data = String.Empty;


            //loop untill we wither run our of viable buffer space (ie only listen for a XX bytes) or we get a \n (0x0A)
            try
            {
                while (data.Length < maxMessageSize)
                {
                    int bytesRec = handler.Receive(bytes, 0, maxMessageSize, SocketFlags.None);
                    data += Encoding.ASCII.GetString(bytes, 0, bytesRec);
                    if (data.IndexOf('\n') > -1)
                    {
                        break;
                    }
                }

                //we got here because we either got the \n (0x0A)....
                if (data.IndexOf('\n') > -1)
                {   //so process the message

                    string command = data.TrimEnd(charsToTrim);

                    switch (command)
                    {
                        case "UPDATE":

                            string statusJSON = JsonConvert.SerializeObject(CurrentStatus);
                            handler.Send(Encoding.ASCII.GetBytes(statusJSON));
                            //handler.Send(Encoding.ASCII.GetBytes("OK"));
                            break;
                        default:
                            break;
                    }

                    handler.Close();
                    killConnectionTimer.Dispose();
                    //handler.Send(Encoding.ASCII.GetBytes(GetLicense(data.Replace("<EOR>", ""))));

                }
                else //or are just reading garbage so we will ignore it;
                {
                    log.Warn("Got too much junk data, is someone trying to DOS us?");
                    Console.WriteLine("Got garbage dumping client.");
                    handler.Close();
                }
            }
            catch (Exception e)
            {
                log.Error(e.Message);
                //I don't think we care as to what happens here
            }
        }


        internal static void KillConnection(Object o)
        {
            Socket h = (Socket)o;
            try
            {
                log.Warn(string.Format("{0} took too long, dropping.", IPAddress.Parse(((IPEndPoint)h.RemoteEndPoint).Address.ToString())));
                Console.WriteLine("{0} took too long, dropping.", IPAddress.Parse(((IPEndPoint)h.RemoteEndPoint).Address.ToString()));
                h.Close();
            }
            catch
            {
                //the socket is probably gone, thats ok just move on
            }
        }



    }
}

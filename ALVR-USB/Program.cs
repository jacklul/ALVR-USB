using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpAdbClient;
using System.Diagnostics;
using IniParser;
using IniParser.Model;
using System.IO.Compression;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ALVRUSB
{
    internal class Program
    {
        public const string VERSION = "0.5.0";
        
        private static string[] deviceNames =
        {
            "monterey",  // Oculus Quest 1
            "vr_monterey",
            "hollywood", // Oculus Quest 2
            "vr_hollywood",
            "pacific",   // Oculus Go
            "vr_pacific",
        };
        
        private static readonly AdbClient client = new AdbClient();
        private static readonly AdbServer server = new AdbServer();
        private static readonly IPEndPoint endPoint = new IPEndPoint(IPAddress.Loopback, AdbClient.AdbServerPort);
        private static readonly FileIniDataParser iniParser = new FileIniDataParser();
        private static readonly LogShellOutputReceiver outputReceiver = new LogShellOutputReceiver();

        private static readonly string currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private static string iniFile = Path.Combine(currentDirectory, Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName) + ".ini");
        private static string logFile = Path.Combine(currentDirectory, Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName) + ".log");
        private static string alvrPath = Path.Combine(currentDirectory, "ALVR Launcher.exe");
        private static string adbPath = Path.Combine(currentDirectory, "adb\\adb.exe");
        private static string connectCommand = null;
        private static string disconnectCommand = null;
        private static string clientActivity = "alvr.client.quest/com.polygraphene.alvr.OvrActivity";
        private static string customDevices = "";
        private static bool debug = false;
        private static bool logging = false;
        private static bool truncateLog = true;
        private static bool noVerify = false;

        private static bool logInitialized = false;
        private static bool adbLaunched = false;
        private static DeviceData currentDevice = null;

        private static void Main()
        {
            Console.Title = typeof(Program).Assembly.GetName().Name + $" v{VERSION}";
            Console.ResetColor();
            Console.SetWindowSize(100, 15);
            Console.SetBufferSize(100, 100);

            handler = new ConsoleEventDelegate(ConsoleEventCallback);
            SetConsoleCtrlHandler(handler, true);

            if (File.Exists(iniFile))
            {
                IniData iniData = iniParser.ReadFile(iniFile);

                string debugKey = iniData.GetKey("debug");
                string logFileKey = iniData.GetKey("logFile");
                string loggingKey = iniData.GetKey("logging");
                string truncateLogKey = iniData.GetKey("truncateLog");
                string noVerifyKey = iniData.GetKey("noVerify");
                string alvrPathKey = iniData.GetKey("alvrPath");
                string adbPathKey = iniData.GetKey("adbPath");
                string connectCommandKey = iniData.GetKey("connectCommand");
                string disconnectCommandKey = iniData.GetKey("disconnectCommand");
                string clientActivityKey = iniData.GetKey("clientActivity");
                string customDeviceNamesKey = iniData.GetKey("customDeviceNames");

                if (!string.IsNullOrEmpty(debugKey))
                    debug = bool.Parse(debugKey);
                
                if (!string.IsNullOrEmpty(logFileKey))
                    logFile = logFileKey;

                if (!string.IsNullOrEmpty(loggingKey))
                    logging = bool.Parse(loggingKey);
                
                if (!string.IsNullOrEmpty(truncateLogKey))
                    truncateLog = bool.Parse(truncateLogKey);

                if (!string.IsNullOrEmpty(noVerifyKey))
                    noVerify = bool.Parse(noVerifyKey);

                if (!string.IsNullOrEmpty(alvrPathKey))
                    alvrPath = alvrPathKey;

                if (!string.IsNullOrEmpty(adbPathKey))
                    adbPath = adbPathKey;
                
                if (!string.IsNullOrEmpty(connectCommandKey))
                    connectCommand = connectCommandKey;
                
                if (!string.IsNullOrEmpty(disconnectCommandKey))
                    disconnectCommand = disconnectCommandKey;

                if (!string.IsNullOrEmpty(clientActivityKey))
                    clientActivity = clientActivityKey;
                
                if (!string.IsNullOrEmpty(customDeviceNamesKey))
                {
                    customDevices = customDeviceNamesKey;
                    deviceNames = deviceNames.Union(customDevices.Split(',')).ToArray();
                }

                if (debug) 
                    LogMessage($"Loaded ini file: {iniFile}", ConsoleColor.DarkGray);
            }

            if (debug) PrintConfig();

            if (currentDirectory == null)
            {
                LogMessage("Path error!", ConsoleColor.Red);
                return;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LogMessage("Unsupported platform!", ConsoleColor.Red);
                return;
            }

            adbPath = Path.Combine(currentDirectory, adbPath);
            if (!File.Exists(adbPath))
            {
                adbPath = WhereSearch("adb.exe");

                if (!File.Exists(adbPath))
                {
                    try
                    {
                        DownloadADB();
                    }
                    catch (Exception exception)
                    {
                        LogMessage(exception.Message, ConsoleColor.Red);
                    }

                    if (!File.Exists(adbPath))
                    {
                        LogMessage("ADB executable not found!", ConsoleColor.Red);
                        return;
                    }
                    else if (debug) LogMessage($"ADB executable downloaded and placed in local directory: {adbPath}", ConsoleColor.DarkGray);
                }
                else if (debug) LogMessage($"ADB executable found in global path: {adbPath}", ConsoleColor.DarkGray);
            }
            else if (debug) LogMessage($"ADB executable found in local directory {adbPath}", ConsoleColor.DarkGray);

            if (!File.Exists(alvrPath))
            {
                if (debug) LogMessage("ALVR Launcher not found", ConsoleColor.DarkGray);
                alvrPath = null;
            }
            else
            {
                if (debug) LogMessage($"ALVR Launcher found: {alvrPath}", ConsoleColor.DarkGray);

                if (!noVerify && !VerifyALVRConfig())
                    return;
            }

            LogMessage($"Initialized {typeof(Program).Assembly.GetName().Name}, version {VERSION}");
            if (debug) LogMessage("Checking initial ADB server status...", ConsoleColor.DarkGray);

            bool initialized = false;
            bool connected = false;
            while (true)
            {
                bool isServerRunning = false;

                try
                {
                    isServerRunning = server.GetStatus().IsRunning;
                }
                catch (SharpAdbClient.Exceptions.AdbException adbException)
                {
                    LogMessage(adbException.Message, ConsoleColor.Red);
                }

                if (!isServerRunning)
                {
                    if (initialized) LogMessage("ADB server connection lost...", ConsoleColor.Red);

                    LogMessage("Starting ADB server...", ConsoleColor.Cyan);
                    server.StartServer(adbPath, false);

                    adbLaunched = true;
                    connected = false;
                }

                if (!connected)
                {
                    try
                    {
                        client.Connect(endPoint);
                        connected = true;
                        if (initialized) LogMessage("ADB server connection restored!", ConsoleColor.Green);
                    }
                    catch (System.Net.Sockets.SocketException socketException)
                    {
                        if (socketException.Message.Contains("127.0.0.1"))
                            LogMessage("Connection to ADB server failed!", ConsoleColor.Red);
                        else
                            LogMessage(socketException.Message, ConsoleColor.Red);

                        return;
                    }
                }

                if (!initialized)
                {
                    var monitor = new DeviceMonitor(new AdbSocket(endPoint));
                    monitor.DeviceConnected += DeviceConnected;
                    monitor.DeviceDisconnected += DeviceDisconnected;

                    LogMessage("Monitoring for devices...", ConsoleColor.Green);
                    monitor.Start();

                    initialized = true;
                }

                Thread.Sleep(100);
            }
        }

        private static void DeviceConnected(object sender, DeviceDataEventArgs e)
        {
            LogMessage($"Connected device: {e.Device.Serial}", ConsoleColor.DarkGreen);
            ProcessDevice(e.Device);
        }

        private static void DeviceDisconnected(object sender, DeviceDataEventArgs e)
        {
            LogMessage($"Disconnected device: {e.Device.Serial}", ConsoleColor.DarkRed);

            if (currentDevice.Serial == e.Device.Serial)
            {
                currentDevice = null;

                if (!string.IsNullOrEmpty(disconnectCommand))
                {
                    if (debug) LogMessage($"Executing \"disconnect\" command: {disconnectCommand}", ConsoleColor.DarkGray);
                    ExecuteCommand(disconnectCommand);
                }
            }
        }
        
        private static void ProcessDevice(DeviceData device)
        {
            int maxTries = 5;
            while (string.IsNullOrEmpty(device.Product)) // DeviceConnected called without product set
            {
                foreach (var deviceData in client.GetDevices().Where(deviceData => device.Serial == deviceData.Serial))
                {
                    if (!string.IsNullOrEmpty(deviceData.Product))
                        device = deviceData;

                    break;
                }

                if (maxTries <= 0)
                    break;

                Thread.Sleep(1000);

                maxTries--;
            }

            if (debug) LogMessage($"DeviceData: {device.Model} {device.Name} {device.Product} {device.Serial}", ConsoleColor.DarkGray);

            if (!deviceNames.Contains(device.Product))
            {
                LogMessage($"Skipped device: {(string.IsNullOrEmpty(device.Product) ? device.Serial : device.Product)}", ConsoleColor.DarkYellow);
                return;
            }

            if (currentDevice != null)
            {
                LogMessage($"Ports are already forwarded for another device: {currentDevice}", ConsoleColor.Yellow);
                return;
            }

            currentDevice = device;

            client.CreateForward(device, 9943, 9943);
            client.CreateForward(device, 9944, 9944);

            LogMessage($"Forwarded ports for device: {device.Serial} ({device.Product})", ConsoleColor.Green);

            if (alvrPath != null)
                LaunchALVRServer();

            if (!string.IsNullOrEmpty(clientActivity))
                LaunchALVRClient();

            if (!string.IsNullOrEmpty(connectCommand))
            {
                if (debug) LogMessage($"Executing \"connect\" command: {connectCommand}", ConsoleColor.DarkGray);
                ExecuteCommand(connectCommand);
            }
        }

        private static void LaunchALVRServer()
        {
            Process[] pname = Process.GetProcessesByName("vrmonitor");

            if (pname.Length == 0)
            {
                if (debug) LogMessage($"Process not found: vrmonitor.exe", ConsoleColor.DarkGray);

                pname = Process.GetProcessesByName(alvrPath);

                if (pname.Length == 0)
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = alvrPath,
                            WorkingDirectory = @currentDirectory,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                        }
                    };
                    process.Start();

                    LogMessage("Launching ALVR Server...", ConsoleColor.Green);
                }
                else if (debug) LogMessage($"Process found: {alvrPath}", ConsoleColor.DarkGray);
            }
            else if (debug) LogMessage($"Process found: vrmonitor.exe", ConsoleColor.DarkGray);
        }

        private static void LaunchALVRClient()
        {
            client.ExecuteRemoteCommand($"pm list packages {clientActivity.Split('/')[0]}", currentDevice, outputReceiver);

            if (string.IsNullOrEmpty(outputReceiver.LastOutput))
            {
                if (debug) LogMessage($"ALVR client is not installed on the device", ConsoleColor.DarkGray);
                return;
            }

            client.ExecuteRemoteCommand($"dumpsys activity | grep {clientActivity}", currentDevice, outputReceiver);

            if (string.IsNullOrEmpty(outputReceiver.LastOutput))
            {
                LogMessage("Launching ALVR client...", ConsoleColor.Green);
                client.ExecuteRemoteCommand($"am start -n {clientActivity}", currentDevice, outputReceiver);
            }
            else if (debug) LogMessage($"ALVR Client activity is already running", ConsoleColor.DarkGray);
        }

        private static string WhereSearch(string filename)
        {
            var paths = new[] { Environment.CurrentDirectory }
                    .Concat(Environment.GetEnvironmentVariable("PATH").Split(';'));
            var extensions = new[] { String.Empty }
                    .Concat(Environment.GetEnvironmentVariable("PATHEXT").Split(';')
                               .Where(e => e.StartsWith(".")));
            var combinations = paths.SelectMany(x => extensions,
                    (path, extension) => Path.Combine(path, filename + extension));
            return combinations.FirstOrDefault(File.Exists);
        }

        private static void LogMessage(string message, ConsoleColor? color = ConsoleColor.White, bool print = true)
        {
            if (!logInitialized)
            {
                if (logFile != null)
                {
                    logFile = Path.Combine(currentDirectory, logFile);

                    if (truncateLog)
                        File.WriteAllText(logFile, "");
                }

                logInitialized = true;
            }

            string datetime = "[" + DateTime.Now.ToString() + "] ";

            if (logging)
                File.AppendAllText(logFile, datetime + message + Environment.NewLine);

            if (print)
            {
                if (color != null)
                    Console.ForegroundColor = (ConsoleColor)color;

                Console.WriteLine(datetime + message);
                Console.ResetColor();
            }
        }

        private static bool ConsoleEventCallback(int eventType)
        {
            if (eventType == 0 || eventType == 2)
            {
                if (adbLaunched)
                {
                    LogMessage("Killing ADB server...");
                    client.KillAdb();
                }

                LogMessage("Exiting...");
            }

            return false;
        }
        static ConsoleEventDelegate handler;
        private delegate bool ConsoleEventDelegate(int eventType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        private static bool ExecuteCommand(string command)
        {
            var cmd = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + command,
                    WorkingDirectory = @currentDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                }
            };

            return cmd.Start();
        }

        private static void PrintConfig()
        {
            LogMessage("Configuration:");
            LogMessage($" debug = {debug}");
            LogMessage($" logFile = {logFile}");
            LogMessage($" logging = {logging}");
            LogMessage($" truncateLog = {truncateLog}");
            LogMessage($" noVerify = {noVerify}");
            LogMessage($" alvrPath = {alvrPath}");
            LogMessage($" adbPath = {adbPath}");
            LogMessage($" connectCommand = {connectCommand}");
            LogMessage($" disconnectCommand = {disconnectCommand}");
            LogMessage($" clientActivity = {clientActivity}");
            LogMessage($" customDevices = {customDevices}");
        }

        private static void DownloadADB()
        {
            var downloadUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
            string[] requiredFiles = { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll" };
            string targetDirectory = Path.Combine(currentDirectory, "adb");

            LogMessage($"Downloading ADB from URL: {downloadUrl}", ConsoleColor.Cyan);

            (new WebClient()).DownloadFile(downloadUrl, Path.Combine(currentDirectory, "adb.zip"));

            if (File.Exists("adb.zip"))
            {
                LogMessage($"Download successful, extracting...", ConsoleColor.Green);

                if (!Directory.Exists(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                    if (debug) LogMessage($"Created directory: {targetDirectory}", ConsoleColor.Cyan);
                }

                using (ZipArchive archive = ZipFile.OpenRead(Path.Combine(currentDirectory, "adb.zip")))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries.Where(e => requiredFiles.Contains(e.Name)))
                    {
                        entry.ExtractToFile(Path.Combine(targetDirectory, entry.Name));
                    }
                }

                bool extractOk = true;
                foreach (string requiredFile in requiredFiles)
                {
                    if (!File.Exists(Path.Combine(targetDirectory, requiredFile)))
                    {
                        extractOk = false;
                        break;
                    }
                }

                if (extractOk)
                    LogMessage($"Extraction successful", ConsoleColor.Green);
                else
                    LogMessage($"Extraction failed", ConsoleColor.Red);
            }
            else
            {
                LogMessage($"Download failed", ConsoleColor.Red);
            }

            File.Delete("adb.zip");
        }

        // Based on https://github.com/dogzz9445/ADBForwarder
        public class LogShellOutputReceiver : IShellOutputReceiver
        {
            public readonly ConcurrentQueue<string> LogShellOutputs = new ConcurrentQueue<string>();

            public bool ParsesErrors => false;
            public string LastOutput = "";

            public void AddOutput(string line)
            {
                LogShellOutputs.Enqueue(line);
            }

            public void Flush()
            {
                LastOutput = "";
                string outputBuffer;

                while (!LogShellOutputs.IsEmpty)
                {
                    if (LogShellOutputs.TryDequeue(out outputBuffer))
                    {
                        LastOutput += outputBuffer + "\n";
                    }
                }

                LastOutput = LastOutput.Trim();
            }
        }

        private static bool VerifyALVRConfig()
        {
            string alvrConfigFile = Path.Combine(Path.GetDirectoryName(alvrPath), "session.json");

            if (File.Exists(alvrConfigFile))
            {
                string data = File.ReadAllText(alvrConfigFile);
                dynamic json = JsonConvert.DeserializeObject<dynamic>(data, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });

                if (json.sessionSettings.connection.clientDiscovery.enabled != false)
                {
                    LogMessage("ALVR configuration error: Client discovery is enabled", ConsoleColor.Red);
                    return false;
                }

                if (((string)json.sessionSettings.connection.streamProtocol.variant).ToLower() != "tcp")
                {
                    LogMessage("ALVR configuration error: Stream protocol is not set to TCP", ConsoleColor.Red);
                    return false;
                }

                if (json.sessionSettings.connection.streamPort != 9944)
                {
                    LogMessage("ALVR configuration error: Streaming port is not set to 9944", ConsoleColor.Red);
                    return false;
                }

                if (!CheckForLocalConnection(json.clientConnections))
                {
                    LogMessage("ALVR configuration error: No client with IP 127.0.0.1 is added", ConsoleColor.Red);
                    return false;
                }
            }

            return true;
        }

        private static bool CheckForLocalConnection(dynamic data)
        {
            foreach (dynamic conn in data)
                foreach (dynamic connA in conn)
                    foreach (dynamic connB in connA.manualIps)
                        if (connB == "127.0.0.1")
                            return true;

            return false;
        }
    }
}
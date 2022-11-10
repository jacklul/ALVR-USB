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

namespace ALVRUSB
{
    internal class Program
    {
        public const string VERSION = "0.3.0";
        
        private static readonly string[] deviceNames =
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

        private static readonly string currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private static string iniFile = Path.Combine(currentDirectory, Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName) + ".ini");
        private static string logFile = Path.Combine(currentDirectory, Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName).ToLower() + ".log");
        private static string alvrPath = Path.Combine(currentDirectory, "ALVR Launcher.exe");
        private static string adbPath = Path.Combine(currentDirectory, "adb\\adb.exe");
        private static string connectCommand = null;
        private static string disconnectCommand = null;
        private static bool debug = false;
        private static bool logging = false;
        private static bool truncateLog = true;

        private static bool adbLaunched = false;
        private static string currentDevice = null;

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
                string alvrPathKey = iniData.GetKey("alvrPath");
                string adbPathKey = iniData.GetKey("adbPath");
                string connectCommandKey = iniData.GetKey("connectCommand");
                string disconnectCommandKey = iniData.GetKey("disconnectCommand");

                if (!string.IsNullOrEmpty(debugKey))
                    debug = bool.Parse(debugKey);
                
                if (!string.IsNullOrEmpty(logFileKey))
                    logFile = logFileKey;

                if (!string.IsNullOrEmpty(loggingKey))
                    logging = bool.Parse(loggingKey);

                if (!string.IsNullOrEmpty(truncateLogKey))
                    truncateLog = bool.Parse(truncateLogKey);

                if (!string.IsNullOrEmpty(alvrPathKey))
                    alvrPath = alvrPathKey;

                if (!string.IsNullOrEmpty(adbPathKey))
                    adbPath = adbPathKey;
                
                if (!string.IsNullOrEmpty(connectCommandKey))
                    connectCommand = connectCommandKey;
                
                if (!string.IsNullOrEmpty(disconnectCommandKey))
                    disconnectCommand = disconnectCommandKey;

                if (debug) 
                    LogMessage($"Loaded ini file: {iniFile}", ConsoleColor.Cyan);
            }

            if (debug) PrintConfig();

            if (currentDirectory == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Path error!");
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
                    LogMessage("ADB executable not found!", ConsoleColor.Red);
                    return;
                }
                else if (debug) LogMessage($"ADB executable found in global path: {adbPath}", ConsoleColor.Cyan);
            }
            else if (debug) LogMessage($"ADB executable found in local directory {adbPath}", ConsoleColor.Cyan);

            if (logFile != null)
            {
                logFile = Path.Combine(currentDirectory, logFile);

                if (truncateLog)
                {
                    File.WriteAllText(logFile, "");
                    if (debug) LogMessage($"Log set to truncate", ConsoleColor.Cyan);
                }
            }

            if (debug)
            {
                if (logging)
                    LogMessage($"Logging is enabled", ConsoleColor.Cyan);
                else
                    LogMessage($"Logging is disabled", ConsoleColor.Cyan);
            }

            if (!File.Exists(alvrPath))
            {
                if (debug) LogMessage("ALVR Launcher not found", ConsoleColor.Cyan);
                alvrPath = null;
            }
            else if (debug) LogMessage($"ALVR Launcher found: {alvrPath}", ConsoleColor.Cyan);

            LogMessage($"Initialized {typeof(Program).Assembly.GetName().Name}, version {VERSION}", null);
            if (debug) LogMessage("Checking initial ADB server status...", ConsoleColor.Cyan);

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

                    LogMessage("Starting ADB server...", ConsoleColor.Yellow);
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
            ForwardPorts(e.Device);
        }

        private static void DeviceDisconnected(object sender, DeviceDataEventArgs e)
        {
            LogMessage($"Disconnected device: {e.Device.Serial}", ConsoleColor.DarkRed);

            if (currentDevice == e.Device.Serial)
            {
                currentDevice = null;

                if (!string.IsNullOrEmpty(disconnectCommand))
                {
                    if (debug) LogMessage($"Executing \"disconnect\" command: {disconnectCommand}", ConsoleColor.Cyan);

                    ExecuteCommand(disconnectCommand);
                }
            }
        }
        
        private static void ForwardPorts(DeviceData device)
        {
            if (debug) LogMessage($"ForwardPorts: {device}", ConsoleColor.Cyan);

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

            if (debug) LogMessage($"DeviceData: {device.Model} {device.Name} {device.Product} {device.Serial}", ConsoleColor.Cyan);

            if (!deviceNames.Contains(device.Product))
            {
                LogMessage($"Skipped device: {(string.IsNullOrEmpty(device.Product) ? device.Serial : device.Product)}", ConsoleColor.Yellow);
                return;
            }

            if (currentDevice != null)
            {
                LogMessage($"Ports are already forwarded for another device: {currentDevice}", ConsoleColor.Red);
                return;
            }

            currentDevice = device.Serial;

            client.CreateForward(device, 9943, 9943);
            client.CreateForward(device, 9944, 9944);

            LogMessage($"Forwarded ports for device: {device.Serial} ({device.Product})", ConsoleColor.Green);

            if (alvrPath != null)
                LaunchALVR();

            if (!string.IsNullOrEmpty(connectCommand))
            {
                if (debug) LogMessage($"Executing \"connect\" command: {connectCommand}", ConsoleColor.Cyan);

                ExecuteCommand(connectCommand);
            }
        }

        private static void LaunchALVR()
        {
            Process[] pname = Process.GetProcessesByName("vrmonitor");

            if (pname.Length == 0)
            {
                if (debug) LogMessage($"Process not found: vrmonitor.exe", ConsoleColor.Cyan);

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

                    LogMessage($"Launching ALVR...", ConsoleColor.Green);
                }
                else if (debug) LogMessage($"Process found: {alvrPath}", ConsoleColor.Cyan);
            }
            else if (debug) LogMessage($"Process found: vrmonitor.exe", ConsoleColor.Cyan);
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

        private static void LogMessage(string message, ConsoleColor? color = ConsoleColor.Yellow, bool print = true)
        {
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
        static ConsoleEventDelegate handler;   // Keeps it from getting garbage collected

        // Pinvoke
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
            LogMessage($" alvrPath = {alvrPath}");
            LogMessage($" adbPath = {adbPath}");
            LogMessage($" connectCommand = {connectCommand}");
            LogMessage($" disconnectCommand = {disconnectCommand}");
        }
    }
}
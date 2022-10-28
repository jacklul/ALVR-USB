# ALVR-USB

This automates steps required for setting up [ALVR through USB connection](https://github.com/alvr-org/ALVR/wiki/Use-ALVR-through-a-USB-connection).

Based on [AtlasTheProto/ADBForwarder](https://github.com/AtlasTheProto/ADBForwarder), modified for personal use.

## Usage

- Place **ALVR-USB.exe** in the same directory as **ALVR Launcher.exe**
- If you don't have `adb` command globally available [download it here](https://dl.google.com/android/repository/platform-tools-latest-windows.zip) - unpack **adb.exe**,  **AdbWinApi.dll** and **AdbWinUsbApi.dll** to **adb** directory (create it in the directory where you placed **ALVR-USB.exe**)
- Run **ALVR-USB.exe** and connect your headset, launch ALVR app inside the headset to start

If the application refuses to start make sure you have **.NET Framework 4.8** installed, you can [get it here](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48).

# ALVR-USB

This automates steps required for setting up [ALVR through USB connection](https://github.com/alvr-org/ALVR/wiki/Use-ALVR-through-a-USB-connection), will also automatically launch ALVR Server on the PC and Client on the headset.

Based on [AtlasTheProto/ADBForwarder](https://github.com/AtlasTheProto/ADBForwarder), modified for personal use.

## Usage

- Download **ALVR-USB.exe** from [latest release](https://github.com/jacklul/ALVR-USB/releases/latest)
- Place it in the same directory as **ALVR Launcher.exe**
- Run it and then connect your headset

If the application refuses to start make sure you have **.NET Framework 4.8 Runtime** installed, you can [get it here](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48).

### Configuration file (`ALVR-USB.ini`)

| Variable | Default | Description |
|----------|---------|-------------|
| debug | false | Should we display extra messages in the console? |
| logFile | "alvr-usb.log" | File for logging |
| logging | false | Enable or disable logging |
| truncateLog | true | Should we truncate log file for each session? |
| alvrPath | "ALVR Launcher.exe" | Path to ALVR Launcher executable |
| adbPath | "adb\adb.exe" | Path to ADB executable |
| connectCommand | "" | Executed when valid device connects |
| disconnectCommand | "" | Executed when previously connected valid device disconnects |
| clientActivity | "alvr.client.quest/com.polygraphene.alvr.OvrActivity" | ALVR client activity to launch when connected |

#### Sample config:
```ini
debug=true
logging=true
logFile=ALVR-USB.log
connectCommand=SoundVolumeView.exe /SetListenToThisDevice "Microphone" 0
disconnectCommand=SoundVolumeView.exe /SetListenToThisDevice "Microphone" 1 && SoundVolumeView /SetPlaybackThroughDevice "Microphone" "CABLE Input"
```

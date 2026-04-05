# Classroom Controller Web

Classroom Controller Web is a classroom management system inspired by Veyon. It combines a browser-based teacher dashboard with a Windows client that runs on student machines, streams the screen, and accepts remote control commands from the server.

## What It Does

- Live screen preview for registered student devices
- Remote control with mouse and keyboard forwarding
- Lock, unlock, freeze, and timed session control
- Admin mode toggle for temporarily disabling student restrictions
- Website blocking for selected domains
- Power actions such as Wake-on-LAN, reboot, and shutdown
- SQLite-backed device state persistence on the server

## Solution Structure

- `Server/`  
  ASP.NET Core server, SignalR hub, WebSocket stream relay, SQLite persistence, and the teacher web UI in `wwwroot/`
- `Client/`  
  Windows WPF client that connects to the server, streams the screen, and applies classroom control actions locally
- `Server/app_data/`  
  Runtime SQLite database location created automatically by the server

## Requirements

- Windows for student client machines
- .NET 8 SDK
- A modern browser for the teacher dashboard
- Devices on the same network segment for streaming and Wake-on-LAN
- Administrator privileges for the client are strongly recommended

The client modifies Windows policies, edits the hosts file for website blocking, blocks user input, and can reboot or shut down the machine. Those actions generally require elevated permissions.

## Quick Start

1. Create local config files from the examples:

```powershell
Copy-Item Server\config.example.json Server\config.json
Copy-Item Client\config.example.json Client\config.json
```

2. Edit `Server/config.json`:
   - Set a private `masterKey`
   - Add the student devices you want to manage
   - Make sure each `mac` matches the active network adapter MAC on the student machine

3. Edit `Client/config.json`:
   - Set `ServerUrl` to the server address, for example `http://192.168.1.10:5000`
   - Set the same `MasterKey` used by the server

4. Start the server:

```powershell
dotnet run --project Server\ClassroomController.Server.csproj
```

5. Start the client on each student machine:

```powershell
dotnet run --project Client\ClassroomController.Client.csproj
```

6. Open the teacher dashboard in a browser:

```text
http://localhost:5000
```

The server is configured to listen on:

- `http://localhost:5000`
- `https://localhost:5001`

The teacher dashboard asks for the master key once and stores it in browser local storage for later visits.

## Configuration

### Server Config

`Server/config.json`

```json
{
  "masterKey": "change-this-key",
  "devices": [
    {
      "mac": "AA-BB-CC-DD-EE-FF",
      "ip": "192.168.1.100",
      "hostname": "Student-PC-01"
    }
  ]
}
```

Fields:

- `masterKey`: shared secret required by the web dashboard and clients
- `devices`: registered student machines
- `mac`: unique identifier used by the server
- `ip`: used for display and Wake-on-LAN targeting
- `hostname`: display name in the dashboard

### Client Config

`Client/config.json`

```json
{
  "ServerUrl": "http://localhost:5000",
  "MasterKey": "change-this-key"
}
```

Fields:

- `ServerUrl`: base URL of the classroom server
- `MasterKey`: must match the server `masterKey`

## Runtime Behavior

- The server automatically applies Entity Framework migrations on startup.
- The SQLite database is created in `Server/app_data/classroom.db`.
- The client writes a local `client_log.txt` file in its output directory.
- Device status and control state are synchronized through SignalR.
- Screen frames are relayed through `/ws/stream`.

## Common Development Commands

Run the full solution from Visual Studio:

```powershell
start Classroom-Controller-Web.sln
```

Publish the server:

```powershell
dotnet publish Server\ClassroomController.Server.csproj -c Release
```

Publish the client:

```powershell
dotnet publish Client\ClassroomController.Client.csproj -c Release
```

## Notes

- Real `config.json` files and runtime database files are intentionally ignored by Git.
- If you change the master key, update both the server config and every client config.
- The current web filter UI is designed around these predefined domains: `youtube.com`, `instagram.com`, `tiktok.com`, and `discord.com`.

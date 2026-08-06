# QianAgent

QianAgent is a local coding-agent desktop application. The backend is a .NET Agent Host process and the desktop renderer is built with Electron, Vue, and Vite.

## Structure

- `backend/Agent.Host`: JSON Lines backend process for the desktop application.
- `backend/Agent.Tools`: workspace, code-search, file-write, and Python tools.
- `desktop`: Electron and Vue desktop frontend.

## Run

Build the backend first:

```powershell
cd backend/Agent.Host
dotnet build
```

Install and start the desktop app:

```powershell
cd desktop
npm.cmd install
npm.cmd start
```

The frontend starts `backend/Agent.Host/bin/Debug/net10.0/Agent.Host.exe` as a local child process. Configure the API endpoint, model, and API key in the desktop application. Do not commit API keys.

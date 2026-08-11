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

On macOS, use npm instead of npm.cmd:

~~~bash
cd desktop
npm install
npm start
~~~

## Package

Create a self-contained Windows portable executable:

~~~powershell
cd desktop
npm.cmd run package:win
~~~

Create macOS DMG and ZIP packages on a Mac:

~~~bash
cd desktop
npm run package:mac:arm64
~~~

Use npm run package:mac:x64 for Intel Macs. The self-contained backend is included, so end users do not need to install .NET. Unsigned builds may be blocked by Gatekeeper; public distribution requires an Apple Developer ID signature and notarization.

The frontend starts the platform-specific Agent.Host executable from `backend/Agent.Host/bin/Debug/net10.0` as a local child process. Configure the API endpoint, model, and API key in the desktop application. Do not commit API keys.

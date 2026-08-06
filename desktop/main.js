const { app, BrowserWindow, dialog, ipcMain } = require("electron");
const { spawn } = require("node:child_process");
const path = require("node:path");
const readline = require("node:readline");
const crypto = require("node:crypto");

let mainWindow;
let hostProcess;

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1360,
    height: 860,
    minWidth: 980,
    minHeight: 640,
    backgroundColor: "#f6f7fb",
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false
    }
  });

  mainWindow.loadFile(path.join(__dirname, "dist", "index.html"));
}

function startHost() {
  if (hostProcess && !hostProcess.killed) {
    return;
  }

  const hostPath = path.resolve(__dirname, "..", "backend", "Agent.Host", "bin", "Debug", "net10.0", "Agent.Host.exe");
  hostProcess = spawn(hostPath, [], { windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });

  readline.createInterface({ input: hostProcess.stdout }).on("line", (line) => {
    try {
      mainWindow?.webContents.send("agent:event", JSON.parse(line));
    } catch {
      mainWindow?.webContents.send("agent:event", { type: "error", message: `Invalid host output: ${line}` });
    }
  });

  readline.createInterface({ input: hostProcess.stderr }).on("line", (line) => {
    mainWindow?.webContents.send("agent:event", { type: "host_log", payload: { text: line } });
  });

  hostProcess.on("exit", (code) => {
    hostProcess = undefined;
    mainWindow?.webContents.send("agent:event", { type: "error", message: `Agent Host exited with code ${code ?? "unknown"}.` });
  });
}

ipcMain.handle("agent:request", (_, request) => {
  startHost();
  if (!hostProcess?.stdin.writable) {
    throw new Error("Agent Host is not available.");
  }

  const id = crypto.randomUUID();
  hostProcess.stdin.write(`${JSON.stringify({ id, ...request })}\n`);
  return id;
});

ipcMain.handle("workspace:select", async () => {
  const result = await dialog.showOpenDialog(mainWindow, { properties: ["openDirectory"] });
  return result.canceled ? null : result.filePaths[0];
});

app.whenReady().then(() => {
  createWindow();
  startHost();
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") {
    app.quit();
  }
});

app.on("before-quit", () => {
  hostProcess?.kill();
});

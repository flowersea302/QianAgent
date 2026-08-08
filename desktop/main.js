const { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme } = require("electron");
const { spawn } = require("node:child_process");
const path = require("node:path");
const readline = require("node:readline");
const crypto = require("node:crypto");

let mainWindow;
let hostProcess;
let titleBarTheme = "system";

function updateWindowTheme() {
  if (!mainWindow) {
    return;
  }

  const useDarkTheme = titleBarTheme === "dark" || (titleBarTheme === "system" && nativeTheme.shouldUseDarkColors);
  mainWindow.setBackgroundColor(useDarkTheme ? "#191a1d" : "#f7f7f8");
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1360,
    height: 860,
    minWidth: 980,
    minHeight: 640,
    backgroundColor: "#f6f7fb",
    frame: false,
    icon: path.join(__dirname, "resources", "qian-agent.png"),
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false
    }
  });

  mainWindow.loadFile(path.join(__dirname, "dist", "index.html"));
  updateWindowTheme();
}

function startHost() {
  if (hostProcess && !hostProcess.killed) {
    return;
  }

  const hostPath = app.isPackaged
    ? path.join(process.resourcesPath, "backend", "Agent.Host.exe")
    : path.resolve(__dirname, "..", "backend", "Agent.Host", "bin", "Debug", "net10.0-windows", "Agent.Host.exe");
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

ipcMain.handle("window:set-theme", (_, theme) => {
  if (["light", "dark", "system"].includes(theme)) {
    titleBarTheme = theme;
    updateWindowTheme();
  }
});

ipcMain.handle("window:minimize", () => mainWindow?.minimize());
ipcMain.handle("window:toggle-maximize", () => {
  if (mainWindow?.isMaximized()) {
    mainWindow.unmaximize();
  }
  else {
    mainWindow?.maximize();
  }
});
ipcMain.handle("window:close", () => mainWindow?.close());

app.whenReady().then(() => {
  Menu.setApplicationMenu(null);
  createWindow();
  startHost();
});

nativeTheme.on("updated", () => {
  if (titleBarTheme === "system") {
    updateWindowTheme();
  }
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") {
    app.quit();
  }
});

app.on("before-quit", () => {
  hostProcess?.kill();
});

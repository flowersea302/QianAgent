const { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme } = require("electron");
const { spawn } = require("node:child_process");
const path = require("node:path");
const readline = require("node:readline");
const crypto = require("node:crypto");

let mainWindow;
let hostProcess;
let titleBarTheme = "system";
let isQuitting = false;
const browserWindows = new Set();

const applicationTitle = "乾Agent";
const applicationIcon = path.join(__dirname, "resources", "qian-agent.png");

function sendToMainWindow(channel, payload) {
  if (!mainWindow || mainWindow.isDestroyed() || mainWindow.webContents.isDestroyed()) {
    return;
  }

  mainWindow.webContents.send(channel, payload);
}

function getWindowBackgroundColor() {
  const useDarkTheme = titleBarTheme === "dark" || (titleBarTheme === "system" && nativeTheme.shouldUseDarkColors);
  return useDarkTheme ? "#191a1d" : "#f7f7f8";
}

function updateWindowTheme() {
  if (!mainWindow) {
    return;
  }

  const backgroundColor = getWindowBackgroundColor();
  mainWindow.setBackgroundColor(backgroundColor);
  for (const browserWindow of browserWindows) {
    browserWindow.setBackgroundColor(backgroundColor);
  }
}

function openBrowserWindow(url) {
  if (!/^https?:\/\//i.test(url)) {
    return;
  }

  const browserWindow = new BrowserWindow({
    width: 1120,
    height: 760,
    minWidth: 720,
    minHeight: 520,
    title: applicationTitle,
    icon: applicationIcon,
    backgroundColor: getWindowBackgroundColor(),
    autoHideMenuBar: true,
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  });
  browserWindows.add(browserWindow);
  browserWindow.on("closed", () => browserWindows.delete(browserWindow));

  browserWindow.webContents.setWindowOpenHandler(({ url: targetUrl }) => {
    openBrowserWindow(targetUrl);
    return { action: "deny" };
  });
  browserWindow.on("page-title-updated", (event, title) => {
    if (!title?.trim() || title === "my-agent-desktop") {
      event.preventDefault();
      browserWindow.setTitle(applicationTitle);
    }
  });
  browserWindow.loadURL(url).catch(() => {
    if (!browserWindow.isDestroyed()) {
      browserWindow.setTitle(`${applicationTitle} - 页面加载失败`);
    }
  });
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1360,
    height: 860,
    minWidth: 980,
    minHeight: 640,
    backgroundColor: "#f6f7fb",
    frame: false,
    icon: applicationIcon,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false
    }
  });

  mainWindow.loadFile(path.join(__dirname, "dist", "index.html"));
  mainWindow.on("closed", () => {
    mainWindow = undefined;
  });
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    openBrowserWindow(url);
    return { action: "deny" };
  });
  updateWindowTheme();
}

function startHost() {
  if (hostProcess && !hostProcess.killed) {
    return;
  }

  const hostExecutable = process.platform === "win32" ? "Agent.Host.exe" : "Agent.Host";
  const hostPath = app.isPackaged
    ? path.join(process.resourcesPath, "backend", hostExecutable)
    : path.resolve(__dirname, "..", "backend", "Agent.Host", "bin", "Debug", "net10.0", hostExecutable);
  hostProcess = spawn(hostPath, [], { windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });

  readline.createInterface({ input: hostProcess.stdout }).on("line", (line) => {
    try {
      sendToMainWindow("agent:event", JSON.parse(line));
    } catch {
      sendToMainWindow("agent:event", { type: "error", message: `Invalid host output: ${line}` });
    }
  });

  readline.createInterface({ input: hostProcess.stderr }).on("line", (line) => {
    sendToMainWindow("agent:event", { type: "host_log", payload: { text: line } });
  });

  hostProcess.on("exit", (code) => {
    hostProcess = undefined;
    if (!isQuitting) {
      sendToMainWindow("agent:event", { type: "error", message: `Agent Host exited with code ${code ?? "unknown"}.` });
    }
  });

  hostProcess.on("error", (error) => {
    if (!isQuitting) {
      sendToMainWindow("agent:event", { type: "error", message: `Agent Host failed to start: ${error.message}` });
    }
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

app.on("activate", () => {
  if (BrowserWindow.getAllWindows().length === 0) {
    createWindow();
  }
});

app.on("before-quit", () => {
  isQuitting = true;
  hostProcess?.kill();
});

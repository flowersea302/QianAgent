const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("agentDesktop", {
  request: (request) => ipcRenderer.invoke("agent:request", request),
  selectWorkspace: () => ipcRenderer.invoke("workspace:select"),
  setWindowTheme: (theme) => ipcRenderer.invoke("window:set-theme", theme),
  minimizeWindow: () => ipcRenderer.invoke("window:minimize"),
  toggleMaximizeWindow: () => ipcRenderer.invoke("window:toggle-maximize"),
  closeWindow: () => ipcRenderer.invoke("window:close"),
  onEvent: (listener) => ipcRenderer.on("agent:event", (_, event) => listener(event))
});

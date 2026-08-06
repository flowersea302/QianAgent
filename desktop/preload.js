const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("agentDesktop", {
  request: (request) => ipcRenderer.invoke("agent:request", request),
  selectWorkspace: () => ipcRenderer.invoke("workspace:select"),
  onEvent: (listener) => ipcRenderer.on("agent:event", (_, event) => listener(event))
});

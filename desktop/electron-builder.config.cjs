module.exports = function createBuilderConfiguration(backendRuntime) {
  return {
    appId: "com.flowersea.qianagent",
    productName: "QianAgent",
    directories: {
      output: "release"
    },
    files: [
      "dist/**/*",
      "main.js",
      "preload.js",
      "package.json"
    ],
    extraResources: [
      {
        from: "../backend/publish/" + backendRuntime,
        to: "backend"
      }
    ],
    win: {
      target: "portable",
      icon: "resources/qian-agent.ico"
    },
    mac: {
      category: "public.app-category.productivity",
      icon: "resources/qian-agent.png"
    }
  };
};

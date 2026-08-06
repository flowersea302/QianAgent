<script setup>
import { computed, nextTick, ref } from "vue";

const initialized = ref(false);
const activeConversationId = ref(null);
const conversations = ref([]);
const messages = ref([]);
const streamingMessages = new Map();
const baseUrl = ref("https://api.thalux.cc/v1");
const model = ref("gpt-5.6-terra");
const apiKey = ref("");
const connectionLabel = ref("未连接");
const workspaceRoot = ref("");
const prompt = ref("");
const messageContainer = ref(null);

const activeConversation = computed(() => conversations.value.find((item) => item.conversationId === activeConversationId.value));
const activeConversationTitle = computed(() => activeConversation.value?.title || activeConversationId.value || "新对话");

function sendRequest(type, payload = {}) {
  return window.agentDesktop.request({ type, payload });
}

function scrollMessages() {
  nextTick(() => {
    if (messageContainer.value) {
      messageContainer.value.scrollTop = messageContainer.value.scrollHeight;
    }
  });
}

function appendMessage(role, content = "") {
  const message = { role, content };
  messages.value.push(message);
  scrollMessages();
  return message;
}

function refreshConversations() {
  if (initialized.value) {
    sendRequest("list_conversations");
  }
}

async function connect() {
  connectionLabel.value = "连接中";
  await sendRequest("initialize", {
    baseUrl: baseUrl.value.trim(),
    model: model.value.trim(),
    apiKey: apiKey.value.trim()
  });
  apiKey.value = "";
}

function createConversation() {
  if (initialized.value) {
    sendRequest("new_conversation");
  }
}

function openConversation(conversationId) {
  sendRequest("open_conversation", { conversationId });
}

function renameConversation() {
  if (!activeConversationId.value) {
    return;
  }

  const title = window.prompt("会话名称", activeConversationTitle.value);
  if (title?.trim()) {
    sendRequest("rename_conversation", { conversationId: activeConversationId.value, title: title.trim() });
  }
}

async function selectWorkspace() {
  const selectedPath = await window.agentDesktop.selectWorkspace();
  if (selectedPath && activeConversationId.value) {
    sendRequest("set_workspace", { conversationId: activeConversationId.value, workspaceRoot: selectedPath });
  }
}

function sendMessage() {
  const content = prompt.value.trim();
  if (!initialized.value || !content) {
    return;
  }

  appendMessage("user", content);
  prompt.value = "";
  sendRequest("chat", { conversationId: activeConversationId.value, message: content });
}

function handleEvent(event) {
  const payload = event.payload || {};

  if (event.type === "initialized") {
    initialized.value = true;
    connectionLabel.value = "已连接";
    createConversation();
    refreshConversations();
    return;
  }

  if (event.type === "conversation_list") {
    conversations.value = payload;
    return;
  }

  if (event.type === "conversation_created" || event.type === "conversation_opened") {
    activeConversationId.value = payload.conversationId;
    workspaceRoot.value = payload.workspaceRoot || "";
    messages.value = event.type === "conversation_opened" ? payload.messages || [] : [];
    if (payload.title) {
      const conversation = conversations.value.find((item) => item.conversationId === payload.conversationId);
      if (conversation) {
        conversation.title = payload.title;
      }
    }
    streamingMessages.clear();
    refreshConversations();
    return;
  }

  if (event.type === "workspace_changed") {
    workspaceRoot.value = payload.workspaceRoot;
    appendMessage("system", `工作区已切换到 ${payload.workspaceRoot}`);
    refreshConversations();
    return;
  }

  if (event.type === "conversation_renamed") {
    const conversation = conversations.value.find((item) => item.conversationId === payload.conversationId);
    if (conversation) {
      conversation.title = payload.title;
    }
    refreshConversations();
    return;
  }

  if (event.type === "chat_started") {
    activeConversationId.value = payload.conversationId;
    workspaceRoot.value = payload.workspaceRoot || "";
    streamingMessages.set(event.id, appendMessage("assistant"));
    return;
  }

  if (event.type === "text_delta") {
    const message = streamingMessages.get(event.id) || appendMessage("assistant");
    message.content += payload.text;
    streamingMessages.set(event.id, message);
    scrollMessages();
    return;
  }

  if (event.type === "completed") {
    streamingMessages.delete(event.id);
    workspaceRoot.value = payload.workspaceRoot || "";
    refreshConversations();
    return;
  }

  if (event.type === "error") {
    connectionLabel.value = initialized.value ? "连接异常" : "连接失败";
    appendMessage("system", `错误：${event.message}`);
  }
}

window.agentDesktop.onEvent(handleEvent);
</script>

<template>
  <main class="app-shell">
    <aside class="sidebar">
      <div class="brand"><span class="brand-mark">M</span><span>乾Agent</span></div>
      <button class="primary-button" type="button" :disabled="!initialized" @click="createConversation">+ 新建对话</button>
      <div class="section-title">会话</div>
      <nav class="conversation-list">
        <button
          v-for="conversation in conversations"
          :key="conversation.conversationId"
          class="conversation-item"
          :class="{ active: conversation.conversationId === activeConversationId }"
          type="button"
          @click="openConversation(conversation.conversationId)"
        >
          {{ conversation.title || conversation.conversationId }}
        </button>
      </nav>
      <div class="sidebar-footer"><span class="status-dot" :class="{ connected: initialized }"></span><span>{{ connectionLabel }}</span></div>
    </aside>

    <section class="chat-panel">
      <header class="chat-header">
        <div class="chat-title"><strong>{{ activeConversationTitle }}</strong><button v-if="activeConversationId" class="rename-button" type="button" @click="renameConversation">重命名</button></div>
        <p>{{ activeConversation ? "本地上下文已保存" : "连接模型后开始对话" }}</p>
      </header>
      <div ref="messageContainer" class="message-list">
        <div v-if="messages.length === 0" class="empty-state"><div class="empty-mark">&gt;_</div><h1>开始一个代码任务</h1><p>选择工作区后，Agent 可以读取、搜索和编写代码。</p></div>
        <article v-for="(message, index) in messages" :key="index" class="message" :class="message.role">
          <div class="message-label">{{ message.role === "user" ? "你" : message.role === "system" ? "系统" : "Agent" }}</div>
          <div class="message-content">{{ message.content }}</div>
        </article>
      </div>
      <form class="composer" @submit.prevent="sendMessage">
        <textarea v-model="prompt" :disabled="!initialized" rows="1" placeholder="输入任务，Enter 发送，Shift+Enter 换行" @keydown.enter.exact.prevent="sendMessage"></textarea>
        <button type="submit" :disabled="!initialized || !prompt.trim()" title="发送">↑</button>
      </form>
    </section>

    <aside class="inspector">
      <section class="inspector-section">
        <h2>连接</h2>
        <label>Base URL<input v-model="baseUrl" /></label>
        <label>模型<input v-model="model" /></label>
        <label>API Key<input v-model="apiKey" type="password" placeholder="仅在当前进程中使用" /></label>
        <button class="secondary-button" type="button" @click="connect">连接模型</button>
      </section>
      <section class="inspector-section">
        <h2>工作区</h2>
        <div class="path-value">{{ workspaceRoot || "尚未选择" }}</div>
        <button class="secondary-button" type="button" :disabled="!initialized || !activeConversationId" @click="selectWorkspace">选择工作区</button>
      </section>
      <section class="inspector-section hint-section"><h2>执行过程</h2><p>Agent 的计划、工具观察和答复会以流式文本显示在对话中。</p></section>
    </aside>
  </main>
</template>

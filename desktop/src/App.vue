<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import MarkdownIt from "markdown-it";
import { Check, Copy, Eye, ListChecks, Minus, Monitor, Moon, Pencil, Send, Settings, Shield, ShieldCheck, Sparkles, SquarePen, Sun, X } from "@lucide/vue";
import appMarkUrl from "../resources/qian-agent-mark.svg";
import appMarkWhiteUrl from "../resources/qian-agent-mark-white.svg";

const markdown = new MarkdownIt({ html: false, linkify: true, breaks: true });
const defaultLinkOpen = markdown.renderer.rules.link_open || ((tokens, index, options, environment, self) => self.renderToken(tokens, index, options));
markdown.renderer.rules.link_open = (tokens, index, options, environment, self) => {
  tokens[index].attrSet("target", "_blank");
  tokens[index].attrSet("rel", "noreferrer");
  return defaultLinkOpen(tokens, index, options, environment, self);
};

const initialized = ref(false);
const isInitializing = ref(false);
const activeConversationId = ref(null);
const conversations = ref([]);
const messages = ref([]);
const streamingMessages = new Map();
const isStreaming = ref(false);
const pendingApproval = ref(null);
const baseUrl = ref("https://api.thalux.cc/v1");
const model = ref("gpt-5.6-terra");
const apiKey = ref("");
const apiKeyConfigured = ref(false);
const modelMenuOpen = ref(false);
const modelEditorOpen = ref(false);
const renameTarget = ref(null);
const renameTitle = ref("");
const renameInput = ref(null);
const conversationMenuId = ref(null);
const connectionLabel = ref("未连接");
const workspaceRoot = ref("");
const prompt = ref("");
const messageContainer = ref(null);
const composerInput = ref(null);
const editingMessage = ref(null);
const editingContent = ref("");
const editingInput = ref(null);
const copiedMessage = ref(null);
const settingsOpen = ref(false);
const theme = ref("system");
const autoApprovedTools = ref([]);
const approvalMenuOpen = ref(false);
const conversationStates = new Map();

const activeConversation = computed(() => conversations.value.find((item) => item.conversationId === activeConversationId.value));
const activeConversationTitle = computed(() => activeConversation.value?.title || activeConversationId.value || "新对话");
const approvalMode = computed(() => {
  if (isToolAutoApproved("write_code") && isToolAutoApproved("execute_python")) {
    return "all";
  }

  return isToolAutoApproved("write_code") ? "write" : "ask";
});

const approvalModeLabel = computed(() => ({
  ask: "请求批准",
  write: "自动批准文件操作",
  all: "自动批准所有工具"
})[approvalMode.value]);

function sendRequest(type, payload = {}) {
  return window.agentDesktop.request({ type, payload });
}

function getConversationState(conversationId) {
  if (!conversationId) {
    return null;
  }

  if (!conversationStates.has(conversationId)) {
    conversationStates.set(conversationId, { messages: [], workspaceRoot: "", isStreaming: false, pendingApproval: null });
  }

  return conversationStates.get(conversationId);
}

function activateConversationState(conversationId) {
  const state = getConversationState(conversationId);
  activeConversationId.value = conversationId;
  messages.value = state?.messages || [];
  workspaceRoot.value = state?.workspaceRoot || "";
  isStreaming.value = state?.isStreaming || false;
  pendingApproval.value = state?.pendingApproval || null;
}

function appendConversationMessage(conversationId, role, content = "", isPending = false) {
  const state = getConversationState(conversationId);
  const message = { role, content, isPending };
  if (state) {
    state.messages.push(message);
  }

  if (conversationId === activeConversationId.value) {
    scrollMessages();
  }

  return message;
}

function refreshActiveConversationState(conversationId) {
  if (conversationId === activeConversationId.value) {
    activateConversationState(conversationId);
  }
}

function applyTheme(value) {
  theme.value = value;
  document.documentElement.dataset.theme = value;
  localStorage.setItem("qian-agent-theme", value);
  window.agentDesktop.setWindowTheme?.(value);
}

function minimizeWindow() {
  window.agentDesktop.minimizeWindow();
}

function toggleMaximizeWindow() {
  window.agentDesktop.toggleMaximizeWindow();
}

function closeWindow() {
  window.agentDesktop.closeWindow();
}

function scrollMessages() {
  nextTick(() => {
    if (messageContainer.value) {
      messageContainer.value.scrollTop = messageContainer.value.scrollHeight;
    }
  });
}

function appendMessage(role, content = "", isPending = false) {
  const message = { role, content, isPending };
  messages.value.push(message);
  scrollMessages();
  return message;
}

function renderMarkdown(content) {
  return markdown.render(content || "");
}

function parseAgentSegments(content) {
  const segments = [];
  const pattern = /\[(Plan|Observation)\]\s*/g;
  let match;
  let position = 0;
  let currentType = "answer";

  while ((match = pattern.exec(content)) !== null) {
    const text = content.slice(position, match.index);
    if (text.trim()) {
      segments.push({ type: currentType, content: text.trim() });
    }

    currentType = match[1].toLowerCase();
    position = pattern.lastIndex;
  }

  const trailingText = content.slice(position);
  if (trailingText.trim()) {
    segments.push({ type: currentType, content: trailingText.trim() });
  }
  else if (currentType !== "answer") {
    segments.push({ type: currentType, content: "正在执行..." });
  }

  return segments;
}

async function copyMessage(message) {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(message.content);
    }
    else {
      const textarea = document.createElement("textarea");
      textarea.value = message.content;
      textarea.style.position = "fixed";
      textarea.style.opacity = "0";
      document.body.append(textarea);
      textarea.select();
      const copied = document.execCommand("copy");
      textarea.remove();
      if (!copied) {
        throw new Error("Clipboard copy failed.");
      }
    }
    copiedMessage.value = message;
    window.setTimeout(() => {
      if (copiedMessage.value === message) {
        copiedMessage.value = null;
      }
    }, 1500);
  }
  catch {
    appendMessage("system", "无法复制消息内容。");
  }
}

function beginMessageEdit(message) {
  if (message.role !== "user" || isStreaming.value) {
    return;
  }

  editingMessage.value = message;
  editingContent.value = message.content;
  nextTick(() => {
    const input = Array.isArray(editingInput.value) ? editingInput.value[0] : editingInput.value;
    input?.focus();
  });
}

function cancelMessageEdit() {
  editingMessage.value = null;
  editingContent.value = "";
}

function submitMessageEdit() {
  const content = editingContent.value.trim();
  const message = editingMessage.value;
  if (!message || !content || isStreaming.value) {
    return;
  }

  message.content = content;
  message.isEdited = true;
  cancelMessageEdit();
  isStreaming.value = true;
  const conversationState = getConversationState(activeConversationId.value);
  if (conversationState) {
    conversationState.isStreaming = true;
  }
  sendRequest("chat", { conversationId: activeConversationId.value, message: content });
}

function refreshConversations() {
  sendRequest("list_conversations");
}

async function connect() {
  if (isInitializing.value) {
    return;
  }

  isInitializing.value = true;
  connectionLabel.value = "连接中";
  await sendRequest("initialize");
}

async function saveModelConfiguration() {
  initialized.value = false;
  await sendRequest("save_model_config", {
    baseUrl: baseUrl.value.trim(),
    model: model.value.trim(),
    apiKey: apiKey.value.trim() || undefined
  });
}

function openModelEditor() {
  modelEditorOpen.value = true;
  modelMenuOpen.value = true;
}

function closeModelMenu() {
  modelMenuOpen.value = false;
  modelEditorOpen.value = false;
}

function createConversation() {
  if (initialized.value) {
    sendRequest("new_conversation");
  }
}

function openConversation(conversationId) {
  conversationMenuId.value = null;
  sendRequest("open_conversation", { conversationId });
}

function beginRename(conversation) {
  if (!conversation || isStreaming.value) {
    return;
  }

  renameTarget.value = conversation;
  conversationMenuId.value = null;
  renameTitle.value = conversation.title || conversation.conversationId;
  nextTick(() => renameInput.value?.select());
}

function renameConversation() {
  beginRename(activeConversation.value);
}

function cancelRename() {
  renameTarget.value = null;
  renameTitle.value = "";
}

function saveRename() {
  const title = renameTitle.value.trim();
  if (!renameTarget.value || !title) {
    return;
  }

  sendRequest("rename_conversation", { conversationId: renameTarget.value.conversationId, title });
  cancelRename();
}

function deleteConversation(conversation = activeConversation.value) {
  if (!conversation || isStreaming.value) {
    return;
  }

  conversationMenuId.value = null;
  const title = conversation.title || conversation.conversationId;
  if (window.confirm(`确定删除“${title}”吗？此操作无法恢复。`)) {
    sendRequest("delete_conversation", { conversationId: conversation.conversationId });
  }
}

function toggleConversationMenu(conversationId) {
  conversationMenuId.value = conversationMenuId.value === conversationId ? null : conversationId;
}

function closeConversationMenuOnOutsideClick(event) {
  if (!event.target.closest(".conversation-row")) {
    conversationMenuId.value = null;
  }

  if (!event.target.closest(".composer-approval-control")) {
    approvalMenuOpen.value = false;
  }
}

async function selectWorkspace(conversationId = activeConversationId.value) {
  const selectedPath = await window.agentDesktop.selectWorkspace();
  if (selectedPath && conversationId) {
    conversationMenuId.value = null;
    sendRequest("set_workspace", { conversationId, workspaceRoot: selectedPath });
  }
}

function sendMessage() {
  const content = prompt.value.trim();
  if (!initialized.value || isStreaming.value || !content) {
    return;
  }

  appendMessage("user", content);
  prompt.value = "";
  isStreaming.value = true;
  const conversationState = getConversationState(activeConversationId.value);
  if (conversationState) {
    conversationState.isStreaming = true;
  }
  sendRequest("chat", { conversationId: activeConversationId.value, message: content });
}

function cancelChat() {
  if (isStreaming.value) {
    sendRequest("cancel_chat", { conversationId: activeConversationId.value });
  }
}

function resolveApproval(approved, remember = false) {
  if (pendingApproval.value) {
    sendRequest("approve_tool", { approvalId: pendingApproval.value.approvalId, approved, remember });
  }
}

function isToolAutoApproved(toolName) {
  return autoApprovedTools.value.includes(toolName);
}

function setToolAutoApproval(toolName, enabled) {
  sendRequest("save_approval_preference", { toolName, enabled });
}

function setApprovalMode(mode) {
  setToolAutoApproval("write_code", mode !== "ask");
  setToolAutoApproval("execute_python", mode === "all");
  approvalMenuOpen.value = false;
}

function handleEvent(event) {
  const payload = event.payload || {};

  if (event.type === "initialized") {
    initialized.value = true;
    baseUrl.value = payload.baseUrl || baseUrl.value;
    model.value = payload.model || model.value;
    connectionLabel.value = "已连接";
    isInitializing.value = false;
    closeModelMenu();
    refreshConversations();
    return;
  }

  if (event.type === "model_config") {
    baseUrl.value = payload.baseUrl || baseUrl.value;
    model.value = payload.model || model.value;
    apiKeyConfigured.value = payload.hasApiKey;
    if (payload.hasApiKey) {
      connect();
    }
    return;
  }

  if (event.type === "approval_preferences") {
    autoApprovedTools.value = payload.autoApprovedTools || [];
    return;
  }

  if (event.type === "model_config_saved") {
    apiKey.value = "";
    apiKeyConfigured.value = true;
    modelEditorOpen.value = false;
    appendMessage("system", "模型配置已保存到本机。");
    connect();
    return;
  }

  if (event.type === "conversation_list") {
    conversations.value = payload;
    if (!activeConversationId.value && payload.length > 0) {
      openConversation(payload[0].conversationId);
    }
    return;
  }

  if (event.type === "conversation_created") {
    conversationStates.set(payload.conversationId, { messages: [], workspaceRoot: "", isStreaming: false, pendingApproval: null });
    activateConversationState(payload.conversationId);
    prompt.value = "";
    nextTick(() => composerInput.value?.focus());
    return;
  }

  if (event.type === "conversation_opened") {
    const state = getConversationState(payload.conversationId);
    if (!state.isStreaming) {
      state.messages = payload.messages || [];
    }
    state.workspaceRoot = payload.workspaceRoot || "";
    activateConversationState(payload.conversationId);
    if (payload.title) {
      const conversation = conversations.value.find((item) => item.conversationId === payload.conversationId);
      if (conversation) {
        conversation.title = payload.title;
      }
    }
    refreshConversations();
    return;
  }

  if (event.type === "workspace_changed") {
    const state = getConversationState(payload.conversationId);
    if (state) {
      state.workspaceRoot = payload.workspaceRoot;
    }
    if (payload.conversationId === activeConversationId.value) {
      workspaceRoot.value = payload.workspaceRoot;
      appendMessage("system", `工作区已切换到 ${payload.workspaceRoot}`);
    }
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

  if (event.type === "conversation_deleted") {
    conversations.value = conversations.value.filter((item) => item.conversationId !== payload.conversationId);
    conversationStates.delete(payload.conversationId);
    if (activeConversationId.value === payload.conversationId) {
      activeConversationId.value = null;
      workspaceRoot.value = "";
      messages.value = [];
      prompt.value = "";
      nextTick(() => composerInput.value?.focus());
    }
    refreshConversations();
    return;
  }

  if (event.type === "chat_started") {
    const state = getConversationState(payload.conversationId);
    if (!activeConversationId.value && state.messages.length === 0 && messages.value.length > 0) {
      state.messages = messages.value;
    }
    state.workspaceRoot = payload.workspaceRoot || "";
    state.isStreaming = true;
    const message = appendConversationMessage(payload.conversationId, "assistant", "", true);
    streamingMessages.set(event.id, { conversationId: payload.conversationId, message });
    if (!activeConversationId.value) {
      activateConversationState(payload.conversationId);
    }
    else {
      refreshActiveConversationState(payload.conversationId);
    }
    refreshConversations();
    return;
  }

  if (event.type === "tool_approval_requested") {
    const state = getConversationState(payload.conversationId);
    state.pendingApproval = payload;
    refreshActiveConversationState(payload.conversationId);
    return;
  }

  if (event.type === "tool_approval_resolved") {
    for (const state of conversationStates.values()) {
      if (state.pendingApproval?.approvalId === payload.approvalId) {
        state.pendingApproval = null;
      }
    }
    refreshActiveConversationState(activeConversationId.value);
    return;
  }

  if (event.type === "cancellation_requested") {
    return;
  }

  if (event.type === "cancelled") {
    const stream = streamingMessages.get(event.id);
    const conversationId = payload?.conversationId || stream?.conversationId;
    if (stream) {
      stream.message.isPending = false;
      streamingMessages.delete(event.id);
    }
    const state = getConversationState(conversationId);
    if (state) {
      state.isStreaming = false;
      state.pendingApproval = null;
      appendConversationMessage(conversationId, "system", event.message || "Chat cancelled.");
      refreshActiveConversationState(conversationId);
    }
    return;
  }

  if (event.type === "text_delta") {
    const stream = streamingMessages.get(event.id);
    const conversationId = payload.conversationId || stream?.conversationId;
    const message = stream?.message || appendConversationMessage(conversationId, "assistant");
    message.isPending = false;
    message.content += payload.text;
    streamingMessages.set(event.id, { conversationId, message });
    if (conversationId === activeConversationId.value) {
      scrollMessages();
    }
    return;
  }

  if (event.type === "completed") {
    const stream = streamingMessages.get(event.id);
    const conversationId = payload.conversationId || stream?.conversationId;
    if (stream) {
      stream.message.isPending = false;
      streamingMessages.delete(event.id);
    }
    const state = getConversationState(conversationId);
    if (state) {
      state.isStreaming = false;
      state.pendingApproval = null;
      state.workspaceRoot = payload.workspaceRoot || "";
      refreshActiveConversationState(conversationId);
    }
    refreshConversations();
    return;
  }

  if (event.type === "error") {
    const stream = streamingMessages.get(event.id);
    const conversationId = payload?.conversationId || stream?.conversationId;
    if (stream) {
      stream.message.isPending = false;
      streamingMessages.delete(event.id);
    }
    isInitializing.value = false;
    const state = getConversationState(conversationId);
    if (state) {
      state.isStreaming = false;
      state.pendingApproval = null;
      if (conversationId !== activeConversationId.value) {
        appendConversationMessage(conversationId, "system", event.message || "Request failed.");
        return;
      }
    }
    isStreaming.value = false;
    pendingApproval.value = null;
    connectionLabel.value = initialized.value ? "连接异常" : "连接失败";
    appendMessage("system", `错误：${event.message}`);
  }
}

window.agentDesktop.onEvent(handleEvent);
onMounted(() => {
  applyTheme(localStorage.getItem("qian-agent-theme") || "system");
  document.addEventListener("pointerdown", closeConversationMenuOnOutsideClick, true);
  sendRequest("get_model_config");
  sendRequest("get_approval_preferences");
  refreshConversations();
});

onBeforeUnmount(() => {
  document.removeEventListener("pointerdown", closeConversationMenuOnOutsideClick, true);
});
</script>

<template>
  <main class="app-shell">
    <header class="window-titlebar">
      <div class="window-drag-region"><img class="window-title-icon window-title-icon-black" :src="appMarkUrl" alt="" /><img class="window-title-icon window-title-icon-white" :src="appMarkWhiteUrl" alt="" /><span>乾Agent</span></div>
      <div class="window-controls">
        <button type="button" title="最小化" @click.stop="minimizeWindow"><Minus :size="16" :stroke-width="1.6" /></button>
        <button type="button" title="最大化或还原" @click.stop="toggleMaximizeWindow"><Copy :size="14" :stroke-width="1.6" /></button>
        <button class="window-close" type="button" title="关闭" @click.stop="closeWindow"><X :size="17" :stroke-width="1.6" /></button>
      </div>
    </header>
    <aside class="sidebar">
      <div class="brand"><span>乾Agent</span></div>
      <button class="primary-button" type="button" :disabled="!initialized" @click="createConversation"><SquarePen :size="16" /> 新对话</button>
      <div class="section-title">会话</div>
      <nav class="conversation-list">
        <div
          v-for="conversation in conversations"
          :key="conversation.conversationId"
          class="conversation-row"
          :class="{ active: conversation.conversationId === activeConversationId }"
        >
          <button class="conversation-item" type="button" @click="openConversation(conversation.conversationId)">{{ conversation.title || conversation.conversationId }}</button>
          <button class="conversation-menu-trigger" type="button" title="会话操作" @click="toggleConversationMenu(conversation.conversationId)">...</button>
          <div v-if="conversationMenuId === conversation.conversationId" class="conversation-menu">
            <button type="button" @click="beginRename(conversation)">重命名</button>
            <button type="button" @click="selectWorkspace(conversation.conversationId)">选择工作区</button>
            <button class="conversation-menu-delete" type="button" @click="deleteConversation(conversation)">删除</button>
          </div>
        </div>
      </nav>
      <div class="sidebar-footer">
        <span class="connection-status"><span class="status-dot" :class="{ connected: initialized }"></span><span>{{ connectionLabel }}</span></span>
        <button class="settings-trigger" type="button" title="设置" @click="settingsOpen = true"><Settings :size="17" /></button>
      </div>
    </aside>

    <section class="chat-panel">
      <header class="chat-header">
        <div class="chat-title"><strong>{{ activeConversationTitle }}</strong></div>
        <p>{{ activeConversation ? "本地上下文已保存" : "输入任务以开始对话" }}</p>
      </header>
      <div ref="messageContainer" class="message-list">
        <div v-if="messages.length === 0" class="empty-state"><div class="empty-mark"><Sparkles :size="40" :stroke-width="1.5" /></div><h1>开始新的对话</h1><p>告诉我你想完成什么。</p></div>
        <article v-for="(message, index) in messages" :key="index" class="message" :class="message.role">
          <div class="message-bubble">
            <div class="message-content" :aria-live="message.isPending ? 'polite' : 'off'">
              <span v-if="message.isPending" class="processing-indicator"><span class="processing-spinner"></span>正在处理请求</span>
              <template v-else-if="editingMessage === message">
                <textarea ref="editingInput" v-model="editingContent" class="message-editor" rows="3" @keydown.esc.prevent="cancelMessageEdit"></textarea>
                <div class="message-edit-actions"><button class="message-edit-cancel" type="button" title="取消" @click="cancelMessageEdit"><X :size="15" /></button><button class="message-edit-send" type="button" title="发送修订" :disabled="!editingContent.trim()" @click="submitMessageEdit"><Send :size="15" /></button></div>
              </template>
              <div v-else-if="message.role === 'assistant'" class="agent-segments">
                <section v-for="(segment, segmentIndex) in parseAgentSegments(message.content)" :key="segmentIndex" class="agent-segment" :class="segment.type">
                  <template v-if="segment.type !== 'answer'">
                    <div class="agent-segment-heading"><ListChecks v-if="segment.type === 'plan'" :size="15" /><Eye v-else :size="15" /><span>{{ segment.type === "plan" ? "计划" : "观察" }}</span></div>
                    <div class="markdown-content" v-html="renderMarkdown(segment.content)"></div>
                  </template>
                  <div v-else class="markdown-content agent-answer" v-html="renderMarkdown(segment.content)"></div>
                </section>
              </div>
              <div v-else class="markdown-content" v-html="renderMarkdown(message.content)"></div>
            </div>
            <div v-if="!message.isPending && editingMessage !== message && message.role === 'user'" class="message-tools">
              <button type="button" :title="copiedMessage === message ? '已复制' : '复制'" @click="copyMessage(message)"><Check v-if="copiedMessage === message" :size="15" /><Copy v-else :size="15" /></button>
              <button type="button" title="编辑" :disabled="isStreaming" @click="beginMessageEdit(message)"><Pencil :size="15" /></button>
            </div>
          </div>
        </article>
      </div>
      <section v-if="pendingApproval" class="approval-panel">
        <div><strong>{{ pendingApproval.summary }}</strong><p>{{ pendingApproval.toolName }}</p></div>
        <div class="approval-actions"><button class="reject-button" type="button" @click="resolveApproval(false)">拒绝</button><button class="approve-button" type="button" @click="resolveApproval(true)">允许</button><button class="approve-button approval-remember-button" type="button" @click="resolveApproval(true, true)"><ShieldCheck :size="15" /> 始终允许</button></div>
      </section>
      <form class="composer" @submit.prevent="sendMessage">
        <div class="composer-main">
          <textarea ref="composerInput" v-model="prompt" :disabled="!initialized || isStreaming" rows="1" placeholder="输入任务，Enter 发送，Shift+Enter 换行" @keydown.enter.exact.prevent="sendMessage"></textarea>
        </div>
        <div class="composer-footer">
          <div class="composer-actions">
            <button class="model-trigger" type="button" :title="`当前模型：${model}`" @click="modelMenuOpen = !modelMenuOpen">{{ model || "配置模型" }}</button>
            <button v-if="isStreaming" class="stop-send-button" type="button" title="停止" @click="cancelChat">■</button>
            <button v-else type="submit" :disabled="!initialized || !prompt.trim()" title="发送">↑</button>
          </div>
          <div class="composer-approval-control">
          <button class="approval-mode-trigger" type="button" :title="`当前审批模式：${approvalModeLabel}`" @click="approvalMenuOpen = !approvalMenuOpen"><ShieldCheck v-if="approvalMode !== 'ask'" :size="14" /><Shield v-else :size="14" /><span>{{ approvalModeLabel }}</span></button>
          <section v-if="approvalMenuOpen" class="approval-mode-menu">
            <button type="button" :class="{ active: approvalMode === 'ask' }" @click="setApprovalMode('ask')"><Shield :size="16" /><span><strong>请求批准</strong><small>每次副作用操作都请求确认</small></span><Check v-if="approvalMode === 'ask'" :size="16" /></button>
            <button type="button" :class="{ active: approvalMode === 'write' }" @click="setApprovalMode('write')"><ShieldCheck :size="16" /><span><strong>自动批准文件操作</strong><small>写入文件不再重复确认</small></span><Check v-if="approvalMode === 'write'" :size="16" /></button>
            <button type="button" :class="{ active: approvalMode === 'all' }" @click="setApprovalMode('all')"><ShieldCheck :size="16" /><span><strong>自动批准所有工具</strong><small>写入文件与执行 Python 不再确认</small></span><Check v-if="approvalMode === 'all'" :size="16" /></button>
          </section>
          </div>
        </div>
        <section v-if="modelMenuOpen" class="model-menu">
          <div class="model-menu-heading"><strong>模型</strong><button type="button" title="关闭" @click="closeModelMenu">×</button></div>
          <p class="model-summary">{{ apiKeyConfigured ? "当前配置已保存到本机" : "尚未保存模型配置" }}</p>
          <button v-if="!modelEditorOpen" class="model-menu-command" type="button" @click="openModelEditor">配置新模型</button>
          <div v-if="modelEditorOpen" class="model-editor">
            <label>Base URL<input v-model="baseUrl" /></label>
            <label>模型<input v-model="model" list="model-options" /></label>
            <datalist id="model-options"><option value="gpt-5.6-terra"></option><option value="gpt-5.6-sol"></option><option value="gpt-4.1"></option></datalist>
            <label>API Key<input v-model="apiKey" type="password" :placeholder="apiKeyConfigured ? '留空则保持当前密钥' : '首次保存时必填'" /></label>
            <button class="model-menu-command primary-model-command" type="button" @click="saveModelConfiguration">保存模型配置</button>
          </div>
        </section>
      </form>
    </section>

    <div v-if="renameTarget" class="dialog-backdrop" @click.self="cancelRename">
      <form class="rename-dialog" @submit.prevent="saveRename">
        <h2>重命名会话</h2>
        <input ref="renameInput" v-model="renameTitle" maxlength="80" aria-label="会话名称" @keydown.esc.prevent="cancelRename" />
        <div class="dialog-actions"><button class="dialog-cancel" type="button" @click="cancelRename">取消</button><button class="dialog-confirm" type="submit" :disabled="!renameTitle.trim()">保存</button></div>
      </form>
    </div>

    <div v-if="settingsOpen" class="dialog-backdrop" @click.self="settingsOpen = false">
      <section class="settings-dialog" role="dialog" aria-modal="true" aria-labelledby="settings-title">
        <div class="settings-dialog-header">
          <div><h2 id="settings-title">设置</h2><p>外观偏好会保存在此设备上。</p></div>
          <button type="button" title="关闭" @click="settingsOpen = false"><X :size="18" /></button>
        </div>
        <div class="settings-section">
          <span class="settings-label">主题</span>
          <div class="theme-options">
            <button type="button" :class="{ active: theme === 'light' }" @click="applyTheme('light')"><Sun :size="17" /><span>浅色</span></button>
            <button type="button" :class="{ active: theme === 'dark' }" @click="applyTheme('dark')"><Moon :size="17" /><span>深色</span></button>
            <button type="button" :class="{ active: theme === 'system' }" @click="applyTheme('system')"><Monitor :size="17" /><span>跟随系统</span></button>
          </div>
        </div>
      </section>
    </div>
  </main>
</template>

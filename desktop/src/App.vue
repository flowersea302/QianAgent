<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import MarkdownIt from "markdown-it";
import { Check, ChevronRight, Copy, Eye, ListChecks, Minus, Monitor, Moon, Pencil, Send, Settings, Shield, ShieldCheck, Sparkles, SquarePen, Sun, X } from "@lucide/vue";
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
const deleteTarget = ref(null);
const conversationMenuId = ref(null);
const connectionLabel = ref("未连接");
const workspaceRoot = ref("");
const prompt = ref("");
const availableCommands = ref([]);
const availableSkills = ref([]);
const promptPaletteIndex = ref(0);
const dismissedPromptPalette = ref("");
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
const queuedTasks = ref([]);
const conversationStatus = ref(null);
const conversationStates = new Map();
const timerNow = ref(Date.now());
const pinnedMessageId = ref(null);
const pinnedSpacerHeight = ref(0);
let elapsedTimer;
let messageSequence = 0;
let pendingTopAlignment = null;

const activeConversation = computed(() => conversations.value.find((item) => item.conversationId === activeConversationId.value));
const activeConversationTitle = computed(() => activeConversation.value?.title || activeConversationId.value || "新对话");
const approvalMode = computed(() => {
  if (isToolAutoApproved("write_code") && isToolAutoApproved("execute_python") && isToolAutoApproved("execute_command") && isToolAutoApproved("access_internet")) {
    return "all";
  }

  return isToolAutoApproved("write_code") ? "write" : "ask";
});

const approvalModeLabel = computed(() => ({
  ask: "请求批准",
  write: "自动批准文件操作",
  all: "自动批准所有工具"
})[approvalMode.value]);

const promptPaletteType = computed(() => {
  const value = prompt.value;
  if (value.startsWith("/") && !/\s/.test(value)) {
    return "command";
  }

  if (value.startsWith("$") && !/\s/.test(value)) {
    return "skill";
  }

  return null;
});

const promptSuggestions = computed(() => {
  const type = promptPaletteType.value;
  const query = prompt.value.slice(1).toLowerCase();
  if (type === "command") {
    return availableCommands.value
      .filter((item) => item.command.slice(1).toLowerCase().includes(query) || item.title.toLowerCase().includes(query))
      .map((item) => ({ type, value: item.command, title: item.title, description: item.description }))
      .slice(0, 8);
  }

  if (type === "skill") {
    return availableSkills.value
      .filter((item) => item.name.toLowerCase().includes(query) || item.description.toLowerCase().includes(query))
      .map((item) => ({ type, value: `$${item.name}`, title: item.name, description: item.description }))
      .slice(0, 8);
  }

  return [];
});

const promptPaletteOpen = computed(() =>
  promptSuggestions.value.length > 0
  && dismissedPromptPalette.value !== prompt.value);

function selectPromptSuggestion(item) {
  prompt.value = `${item.value} `;
  promptPaletteIndex.value = 0;
  dismissedPromptPalette.value = prompt.value;
  nextTick(() => composerInput.value?.focus());
}

function handleComposerKeydown(event) {
  if (promptPaletteOpen.value) {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const direction = event.key === "ArrowDown" ? 1 : -1;
      promptPaletteIndex.value = (promptPaletteIndex.value + direction + promptSuggestions.value.length) % promptSuggestions.value.length;
      return;
    }

    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      selectPromptSuggestion(promptSuggestions.value[promptPaletteIndex.value] || promptSuggestions.value[0]);
      return;
    }

    if (event.key === "Escape") {
      event.preventDefault();
      dismissedPromptPalette.value = prompt.value;
      return;
    }
  }

  if (event.key === "Enter" && !event.shiftKey) {
    event.preventDefault();
    sendMessage();
  }
}

function resizeComposerInput() {
  const input = composerInput.value;
  if (!input) {
    return;
  }

  input.style.height = "auto";
  input.style.height = `${input.scrollHeight}px`;
  input.style.overflowY = input.scrollHeight > input.clientHeight ? "auto" : "hidden";
}

function handleWindowResize() {
  resizeComposerInput();
}

watch(prompt, () => nextTick(resizeComposerInput));

function sendRequest(type, payload = {}) {
  return window.agentDesktop.request({ type, payload });
}

function createConversationState() {
  return {
    messages: [],
    hasLoadedMessages: false,
    workspaceRoot: "",
    isStreaming: false,
    pendingApproval: null,
    queuedTasks: [],
    status: null,
    scrollTop: null,
    shouldAutoScroll: true,
    pinnedMessageId: null,
    scrollSpacerHeight: 0
  };
}

function getConversationState(conversationId) {
  if (!conversationId) {
    return null;
  }

  if (!conversationStates.has(conversationId)) {
    conversationStates.set(conversationId, createConversationState());
  }

  return conversationStates.get(conversationId);
}

function activateConversationState(conversationId) {
  saveActiveScrollPosition();
  const state = getConversationState(conversationId);
  activeConversationId.value = conversationId;
  messages.value = state?.messages || [];
  workspaceRoot.value = state?.workspaceRoot || "";
  isStreaming.value = state?.isStreaming || false;
  pendingApproval.value = state?.pendingApproval || null;
  queuedTasks.value = [...(state?.queuedTasks || [])];
  conversationStatus.value = state?.status || null;
  pinnedMessageId.value = state?.pinnedMessageId || null;
  pinnedSpacerHeight.value = state?.scrollSpacerHeight || 0;
  nextTick(() => {
    if (messageContainer.value) {
      messageContainer.value.scrollTop = Number.isFinite(state?.scrollTop)
        ? state.scrollTop
        : messageContainer.value.scrollHeight;
    }
  });
}

function appendConversationMessage(conversationId, role, content = "", isPending = false) {
  const state = getConversationState(conversationId);
  const message = { id: ++messageSequence, role, content, isPending };
  if (state) {
    state.hasLoadedMessages = true;
    state.messages.push(message);
  }

  if (conversationId === activeConversationId.value) {
    scrollMessages();
  }

  return message;
}

function refreshActiveConversationState(conversationId) {
  if (conversationId === activeConversationId.value) {
    const state = getConversationState(conversationId);
    workspaceRoot.value = state?.workspaceRoot || "";
    isStreaming.value = state?.isStreaming || false;
    pendingApproval.value = state?.pendingApproval || null;
    queuedTasks.value = [...(state?.queuedTasks || [])];
    conversationStatus.value = state?.status || null;
    pinnedMessageId.value = state?.pinnedMessageId || null;
    pinnedSpacerHeight.value = state?.scrollSpacerHeight || 0;
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

function scrollMessages(force = false) {
  nextTick(() => {
    const state = getConversationState(activeConversationId.value);
    if (messageContainer.value && (force || state?.shouldAutoScroll !== false)) {
      messageContainer.value.scrollTop = messageContainer.value.scrollHeight;
      if (state) {
        state.scrollTop = messageContainer.value.scrollTop;
        state.shouldAutoScroll = true;
      }
    }
  });
}

function isNearMessageListBottom() {
  if (!messageContainer.value) {
    return false;
  }

  const { scrollHeight, scrollTop, clientHeight } = messageContainer.value;
  return scrollHeight - scrollTop - clientHeight <= 80;
}

function alignMessageToTop(message, conversationId = activeConversationId.value) {
  nextTick(() => {
    const state = getConversationState(conversationId);
    const container = messageContainer.value;
    const element = container?.querySelector(`[data-message-id="${message.id}"]`);
    if (!state || !container || !element) {
      return;
    }

    const align = () => {
      const messageTop = element.getBoundingClientRect().top;
      const containerTop = container.getBoundingClientRect().top;
      const targetScrollTop = Math.max(0, container.scrollTop + messageTop - containerTop);
      const maximumScrollTop = container.scrollHeight - container.clientHeight;
      const requiredSpacer = Math.ceil(Math.max(0, targetScrollTop - maximumScrollTop));
      if (requiredSpacer > state.scrollSpacerHeight) {
        state.scrollSpacerHeight = requiredSpacer + 16;
        pinnedSpacerHeight.value = state.scrollSpacerHeight;
        nextTick(() => window.requestAnimationFrame(align));
        return;
      }

      container.scrollTop = targetScrollTop;
      state.scrollTop = container.scrollTop;
      state.shouldAutoScroll = false;
    };

    align();
    window.requestAnimationFrame(align);
    window.setTimeout(align, 50);
    window.setTimeout(align, 180);
  });
}

function syncPinnedSpacer(conversationId = activeConversationId.value) {
  if (conversationId !== activeConversationId.value) {
    return;
  }

  nextTick(() => {
    const state = getConversationState(conversationId);
    const container = messageContainer.value;
    const element = container?.querySelector(`[data-message-id="${state?.pinnedMessageId}"]`);
    if (!state?.pinnedMessageId || !container || !element) {
      return;
    }

    const targetScrollTop = Math.max(0, container.scrollTop + element.getBoundingClientRect().top - container.getBoundingClientRect().top);
    const contentScrollHeight = container.scrollHeight - state.scrollSpacerHeight;
    const requiredSpacer = Math.ceil(Math.max(0, targetScrollTop - (contentScrollHeight - container.clientHeight)));
    const spacerHeight = requiredSpacer > 0 ? requiredSpacer + 16 : 0;
    if (spacerHeight !== state.scrollSpacerHeight) {
      state.scrollSpacerHeight = spacerHeight;
      pinnedSpacerHeight.value = spacerHeight;
    }
  });
}

function saveActiveScrollPosition() {
  const state = getConversationState(activeConversationId.value);
  if (state && messageContainer.value) {
    state.scrollTop = messageContainer.value.scrollTop;
    const distanceToBottom = messageContainer.value.scrollHeight - messageContainer.value.scrollTop - messageContainer.value.clientHeight;
    state.shouldAutoScroll = distanceToBottom <= 80;
  }
}

function appendMessage(role, content = "", isPending = false, forceScroll = role === "user") {
  const message = { id: ++messageSequence, role, content, isPending };
  messages.value.push(message);
  scrollMessages(forceScroll);
  return message;
}

function renderMarkdown(content) {
  return markdown.render(content || "");
}

function formatElapsed(message) {
  if (!message?.startedAt) {
    return "0 秒";
  }

  const endTime = message.completedAt || timerNow.value;
  const seconds = Math.max(0, Math.floor((endTime - message.startedAt) / 1000));
  if (seconds < 60) {
    return `${seconds} 秒`;
  }

  const minutes = Math.floor(seconds / 60);
  return `${minutes} 分 ${seconds % 60} 秒`;
}

function parseAgentSegments(content, isStreaming = false) {
  const segments = [];
  const pattern = /\[(Plan|Observation)\]\s*/g;
  let match;
  let position = 0;
  let currentType = "answer";

  const appendSegment = (type, text) => {
    const normalizedText = text.trim();
    if (!normalizedText) {
      return;
    }

    const visibleText = type === "answer" && isStreaming
      ? getCompletedSentences(normalizedText)
      : normalizedText;
    if (visibleText) {
      segments.push({ type, content: visibleText });
    }
  };

  while ((match = pattern.exec(content)) !== null) {
    const text = content.slice(position, match.index);
    appendSegment(currentType, text);

    currentType = match[1].toLowerCase();
    position = pattern.lastIndex;
  }

  const trailingText = content.slice(position);
  if (trailingText.trim()) {
    appendSegment(currentType, trailingText);
  }
  else if (currentType !== "answer") {
    segments.push({ type: currentType, content: "正在执行..." });
  }

  return segments;
}

function getCompletedSentences(content) {
  const lastSentenceEnd = Math.max(
    content.lastIndexOf("。"),
    content.lastIndexOf("！"),
    content.lastIndexOf("？"),
    content.lastIndexOf("!"),
    content.lastIndexOf("?"),
    content.lastIndexOf("\n")
  );
  return lastSentenceEnd < 0 ? "" : content.slice(0, lastSentenceEnd + 1).trim();
}

function getAssistantAnswer(message) {
  const answer = parseAgentSegments(message.content || "", message.isStreaming)
    .filter((segment) => segment.type === "answer")
    .map((segment) => segment.content)
    .join("\n\n");
  if (answer || message.isStreaming) {
    return answer;
  }

  return message.content || "";
}

function toggleMessageProgress(message) {
  message.detailsExpanded = !message.detailsExpanded;
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
  deleteTarget.value = conversation;
}

function cancelDelete() {
  deleteTarget.value = null;
}

function confirmDelete() {
  if (!deleteTarget.value) {
    return;
  }

  sendRequest("delete_conversation", { conversationId: deleteTarget.value.conversationId });
  cancelDelete();
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
  if (!initialized.value || !content) {
    return;
  }

  if (content.toLowerCase() === "/status") {
    prompt.value = "";
    if (activeConversationId.value) {
      sendRequest("get_conversation_status", { conversationId: activeConversationId.value });
    }
    return;
  }

  const activeState = getConversationState(activeConversationId.value);
  const shouldQueue = activeState?.isStreaming || false;
  const shouldAlignMessage = !shouldQueue && (isNearMessageListBottom() || activeState?.shouldAutoScroll !== false);
  if (shouldAlignMessage) {
    if (activeState) {
      activeState.shouldAutoScroll = false;
    }
  }

  const userMessage = shouldQueue
    ? { id: ++messageSequence, role: "user", content, isPending: false }
    : appendMessage("user", content, false, !shouldAlignMessage);
  if (shouldAlignMessage) {
    if (activeState) {
      activeState.pinnedMessageId = userMessage.id;
      activeState.scrollSpacerHeight = 0;
    }
    pinnedMessageId.value = userMessage.id;
    pinnedSpacerHeight.value = 0;
    pendingTopAlignment = { messageId: userMessage.id };
  }
  prompt.value = "";
  isStreaming.value = true;
  const conversationState = getConversationState(activeConversationId.value);
  if (conversationState) {
    conversationState.isStreaming = true;
  }
  sendRequest("chat", { conversationId: activeConversationId.value, message: content, clientMessageId: String(userMessage.id) });
}

function closeConversationStatus() {
  const state = getConversationState(activeConversationId.value);
  if (state) {
    state.status = null;
  }
  conversationStatus.value = null;
}

function formatTokenCount(value) {
  if (!Number.isFinite(value)) {
    return "不可用";
  }

  if (value >= 1000) {
    return `${(value / 1000).toFixed(value >= 10000 ? 0 : 1)}K`;
  }

  return String(value);
}

function removeQueuedTask(task) {
  if (!task || task.isRemoving || !activeConversationId.value) {
    return;
  }

  task.isRemoving = true;
  queuedTasks.value = [...queuedTasks.value];
  sendRequest("remove_queued_chat", {
    conversationId: activeConversationId.value,
    queueItemId: task.requestId
  }).catch(() => {
    task.isRemoving = false;
    queuedTasks.value = [...queuedTasks.value];
  });
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
  setToolAutoApproval("execute_command", mode === "all");
  setToolAutoApproval("access_internet", mode === "all");
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

  if (event.type === "command_list") {
    availableCommands.value = payload.commands || [];
    return;
  }

  if (event.type === "skill_list") {
    availableSkills.value = payload.skills || [];
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
    const state = createConversationState();
    state.hasLoadedMessages = true;
    conversationStates.set(payload.conversationId, state);
    activateConversationState(payload.conversationId);
    prompt.value = "";
    nextTick(() => composerInput.value?.focus());
    return;
  }

  if (event.type === "conversation_opened") {
    const state = getConversationState(payload.conversationId);
    if (!state.hasLoadedMessages && !state.isStreaming) {
      state.messages = payload.messages || [];
      state.hasLoadedMessages = true;
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
      queuedTasks.value = [];
      conversationStatus.value = null;
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
    state.hasLoadedMessages = true;
    state.workspaceRoot = payload.workspaceRoot || "";
    state.isStreaming = true;
    if (state.status) {
      state.status.isProcessing = true;
    }
    if (pendingTopAlignment?.messageId) {
      state.pinnedMessageId = pendingTopAlignment.messageId;
    }
    const message = appendConversationMessage(payload.conversationId, "assistant", "", true);
    message.isStreaming = true;
    message.startedAt = Date.now();
    message.progressItems = [];
    message.detailsExpanded = true;
    streamingMessages.set(event.id, { conversationId: payload.conversationId, message });
    if (pendingTopAlignment?.messageId) {
      const userMessage = state.messages.find((item) => item.id === pendingTopAlignment.messageId);
      if (userMessage) {
        alignMessageToTop(userMessage, payload.conversationId);
      }
      pendingTopAlignment = null;
    }
    if (!activeConversationId.value) {
      activateConversationState(payload.conversationId);
    }
    else {
      refreshActiveConversationState(payload.conversationId);
    }
    refreshConversations();
    return;
  }

  if (event.type === "chat_queued") {
    const state = getConversationState(payload.conversationId);
    const task = { requestId: event.id, clientMessageId: payload.clientMessageId, message: payload.message, position: payload.position };
    const existingIndex = state.queuedTasks.findIndex((item) => item.requestId === event.id);
    if (existingIndex >= 0) {
      state.queuedTasks.splice(existingIndex, 1, task);
    }
    else {
      state.queuedTasks.push(task);
    }
    state.queuedTasks.forEach((item, index) => { item.position = index + 1; });
    if (state.status) {
      state.status.queuedCount = state.queuedTasks.length;
    }
    refreshActiveConversationState(payload.conversationId);
    return;
  }

  if (event.type === "chat_dequeued") {
    const state = getConversationState(payload.conversationId);
    const dequeuedTask = state.queuedTasks.find((item) => item.requestId === event.id);
    if (dequeuedTask) {
      const userMessage = appendConversationMessage(payload.conversationId, "user", dequeuedTask.message);
      const clientMessageId = Number(dequeuedTask.clientMessageId);
      if (Number.isFinite(clientMessageId)) {
        userMessage.id = clientMessageId;
        messageSequence = Math.max(messageSequence, clientMessageId);
      }
    }
    state.queuedTasks = state.queuedTasks.filter((item) => item.requestId !== event.id);
    state.queuedTasks.forEach((item, index) => { item.position = index + 1; });
    if (state.status) {
      state.status.queuedCount = state.queuedTasks.length;
      state.status.isProcessing = true;
    }
    refreshActiveConversationState(payload.conversationId);
    return;
  }

  if (event.type === "chat_queue_item_removed") {
    const state = getConversationState(payload.conversationId);
    if (payload.removed) {
      const removedTask = state.queuedTasks.find((item) => item.requestId === payload.queueItemId);
      state.queuedTasks = state.queuedTasks.filter((item) => item.requestId !== payload.queueItemId);
      if (removedTask?.clientMessageId) {
        state.messages = state.messages.filter((message) => String(message.id) !== removedTask.clientMessageId);
        if (payload.conversationId === activeConversationId.value) {
          messages.value = state.messages;
        }
      }
      state.queuedTasks.forEach((item, index) => { item.position = index + 1; });
    }
    else {
      const task = state.queuedTasks.find((item) => item.requestId === payload.queueItemId);
      if (task) {
        task.isRemoving = false;
      }
    }
    if (state.status) {
      state.status.queuedCount = state.queuedTasks.length;
    }
    refreshActiveConversationState(payload.conversationId);
    return;
  }

  if (event.type === "conversation_status") {
    const state = getConversationState(payload.conversationId);
    state.status = payload;
    refreshActiveConversationState(payload.conversationId);
    return;
  }

  if (event.type === "agent_progress") {
    const stream = streamingMessages.get(event.id);
    const conversationId = payload.conversationId || stream?.conversationId;
    const message = stream?.message || appendConversationMessage(conversationId, "assistant", "", true);
    message.isPending = false;
    message.isStreaming = true;
    message.detailsExpanded = true;
    message.progressItems ||= [];
    message.progressItems.push({
      id: ++messageSequence,
      type: payload.stage === "plan" ? "plan" : "observation",
      content: payload.text
    });
    streamingMessages.set(event.id, { conversationId, message });
    if (conversationId === activeConversationId.value) {
      scrollMessages();
      syncPinnedSpacer(conversationId);
    }
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
      stream.message.isStreaming = false;
      stream.message.completedAt = Date.now();
      stream.message.detailsExpanded = false;
      streamingMessages.delete(event.id);
    }
    const state = getConversationState(conversationId);
    if (state) {
      state.isStreaming = state.queuedTasks.length > 0;
      state.pendingApproval = null;
      if (state.status) {
        state.status.isProcessing = state.isStreaming;
        state.status.queuedCount = state.queuedTasks.length;
      }
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
    message.isStreaming = true;
    message.content += payload.text;
    streamingMessages.set(event.id, { conversationId, message });
    if (conversationId === activeConversationId.value) {
      scrollMessages();
      syncPinnedSpacer(conversationId);
    }
    return;
  }

  if (event.type === "completed") {
    const stream = streamingMessages.get(event.id);
    const conversationId = payload.conversationId || stream?.conversationId;
    if (stream) {
      stream.message.isPending = false;
      stream.message.isStreaming = false;
      stream.message.completedAt = Date.now();
      stream.message.detailsExpanded = false;
      streamingMessages.delete(event.id);
    }
    const state = getConversationState(conversationId);
    if (state) {
      state.isStreaming = state.queuedTasks.length > 0;
      state.pendingApproval = null;
      state.workspaceRoot = payload.workspaceRoot || "";
      syncPinnedSpacer(conversationId);
      refreshActiveConversationState(conversationId);
      if (state.status) {
        sendRequest("get_conversation_status", { conversationId });
      }
    }
    refreshConversations();
    return;
  }

  if (event.type === "error") {
    const stream = streamingMessages.get(event.id);
    const conversationId = payload?.conversationId || stream?.conversationId;
    if (stream) {
      stream.message.isPending = false;
      stream.message.isStreaming = false;
      stream.message.completedAt = Date.now();
      stream.message.detailsExpanded = false;
      streamingMessages.delete(event.id);
    }
    isInitializing.value = false;
    const state = getConversationState(conversationId);
    if (state) {
      state.isStreaming = state.queuedTasks.length > 0;
      state.pendingApproval = null;
      if (conversationId !== activeConversationId.value) {
        appendConversationMessage(conversationId, "system", event.message || "Request failed.");
        return;
      }
    }
    isStreaming.value = state?.isStreaming || false;
    pendingApproval.value = null;
    connectionLabel.value = initialized.value ? "连接异常" : "连接失败";
    appendMessage("system", `错误：${event.message}`);
  }
}

window.agentDesktop.onEvent(handleEvent);
onMounted(() => {
  elapsedTimer = window.setInterval(() => {
    timerNow.value = Date.now();
  }, 1000);
  applyTheme(localStorage.getItem("qian-agent-theme") || "system");
  document.addEventListener("pointerdown", closeConversationMenuOnOutsideClick, true);
  window.addEventListener("resize", handleWindowResize);
  sendRequest("get_model_config");
  sendRequest("get_approval_preferences");
  sendRequest("list_commands");
  sendRequest("list_skills");
  refreshConversations();
  nextTick(resizeComposerInput);
});

onBeforeUnmount(() => {
  window.clearInterval(elapsedTimer);
  document.removeEventListener("pointerdown", closeConversationMenuOnOutsideClick, true);
  window.removeEventListener("resize", handleWindowResize);
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
      <div ref="messageContainer" class="message-list" @scroll.passive="saveActiveScrollPosition">
        <div v-if="messages.length === 0" class="empty-state"><div class="empty-mark"><Sparkles :size="40" :stroke-width="1.5" /></div><h1>开始新的对话</h1><p>告诉我你想完成什么。</p></div>
        <article v-for="(message, index) in messages" :key="message.id || index" class="message" :class="message.role" :data-message-id="message.id">
          <div class="message-bubble">
            <div v-if="message.role === 'assistant' && (message.isStreaming || message.completedAt)" class="response-duration">
              <Transition name="elapsed-state" mode="out-in"><span :key="message.completedAt ? 'completed' : 'processing'">{{ message.isStreaming ? "已处理" : "耗时" }} {{ formatElapsed(message) }}</span></Transition>
              <button v-if="message.completedAt && message.progressItems?.length" class="response-details-toggle" type="button" :title="message.detailsExpanded ? '收起执行过程' : '展开执行过程'" :aria-expanded="message.detailsExpanded" @click="toggleMessageProgress(message)"><ChevronRight :size="15" :class="{ expanded: message.detailsExpanded }" /></button>
            </div>
            <div class="message-content" :aria-live="message.isPending ? 'polite' : 'off'">
              <span v-if="message.isPending" class="processing-indicator"><span class="processing-spinner"></span>正在处理请求</span>
              <template v-else-if="editingMessage === message">
                <textarea ref="editingInput" v-model="editingContent" class="message-editor" rows="3" @keydown.esc.prevent="cancelMessageEdit"></textarea>
                <div class="message-edit-actions"><button class="message-edit-cancel" type="button" title="取消" @click="cancelMessageEdit"><X :size="15" /></button><button class="message-edit-send" type="button" title="发送修订" :disabled="!editingContent.trim()" @click="submitMessageEdit"><Send :size="15" /></button></div>
              </template>
              <div v-else-if="message.role === 'assistant'" class="assistant-response">
                <div v-if="message.progressItems?.length && (message.isStreaming || message.detailsExpanded)" class="agent-segments">
                  <section v-for="item in message.progressItems" :key="item.id" class="agent-segment" :class="item.type">
                    <div class="agent-segment-heading"><ListChecks v-if="item.type === 'plan'" :size="15" /><Eye v-else :size="15" /><span>{{ item.type === "plan" ? "计划" : "观察" }}</span></div>
                    <div class="markdown-content" v-html="renderMarkdown(item.content)"></div>
                  </section>
                </div>
                <div v-if="getAssistantAnswer(message)" class="markdown-content agent-answer" v-html="renderMarkdown(getAssistantAnswer(message))"></div>
              </div>
              <div v-else class="markdown-content" v-html="renderMarkdown(message.content)"></div>
            </div>
            <div v-if="message.role === 'assistant' && !message.isPending && message.isStreaming" class="response-progress"><span class="processing-spinner"></span><span>正在继续处理</span></div>
            <div v-if="!message.isPending && editingMessage !== message && message.role === 'user'" class="message-tools">
              <button type="button" :title="copiedMessage === message ? '已复制' : '复制'" @click="copyMessage(message)"><Check v-if="copiedMessage === message" :size="15" /><Copy v-else :size="15" /></button>
              <button type="button" title="编辑" :disabled="isStreaming" @click="beginMessageEdit(message)"><Pencil :size="15" /></button>
            </div>
          </div>
        </article>
        <div v-if="pinnedMessageId" class="message-scroll-spacer" :style="{ height: `${pinnedSpacerHeight}px` }" aria-hidden="true"></div>
      </div>
      <section v-if="pendingApproval" class="approval-panel">
        <div><strong>{{ pendingApproval.summary }}</strong><p>{{ pendingApproval.toolName }}</p></div>
        <div class="approval-actions"><button class="reject-button" type="button" @click="resolveApproval(false)">拒绝</button><button class="approve-button" type="button" @click="resolveApproval(true)">允许</button><button class="approve-button approval-remember-button" type="button" @click="resolveApproval(true, true)"><ShieldCheck :size="15" /> 始终允许</button></div>
      </section>
      <form class="composer" @submit.prevent="sendMessage">
        <section v-if="promptPaletteOpen" class="prompt-palette" aria-label="输入建议">
          <div class="prompt-palette-heading">{{ promptPaletteType === "command" ? "可用指令" : "本机 Skills" }}</div>
          <button
            v-for="(item, index) in promptSuggestions"
            :key="item.value"
            type="button"
            :class="{ active: index === promptPaletteIndex }"
            @pointerdown.prevent
            @click="selectPromptSuggestion(item)"
          >
            <span class="prompt-palette-icon"><ListChecks v-if="item.type === 'command'" :size="15" /><Sparkles v-else :size="15" /></span>
            <span class="prompt-palette-copy"><strong>{{ item.value }}</strong><small>{{ item.description }}</small></span>
          </button>
        </section>
        <section v-if="conversationStatus" class="conversation-status-panel" aria-label="对话状态">
          <div class="conversation-status-heading"><strong>状态</strong><button type="button" @click="closeConversationStatus">关闭</button></div>
          <dl>
            <div><dt>会话</dt><dd>{{ conversationStatus.conversationId }}</dd></div>
            <div><dt>上下文</dt><dd><span v-if="conversationStatus.tokenCountSource === 'model'">模型报告</span><span v-else-if="conversationStatus.tokenCountSource === 'model_plus_estimate'">模型报告 + 新消息估算</span><span v-else>本地估算</span> {{ formatTokenCount(conversationStatus.contextTokenCount ?? conversationStatus.estimatedTokens) }} Token<span v-if="conversationStatus.contextWindowTokens"> / {{ formatTokenCount(conversationStatus.contextWindowTokens) }}</span><span v-else>，容量上限未配置</span></dd></div>
            <div><dt>历史</dt><dd>{{ conversationStatus.messageCount }} 条消息<span v-if="conversationStatus.compressedMessageCount">，已压缩 {{ conversationStatus.compressedMessageCount }} 条</span></dd></div>
            <div><dt>任务</dt><dd>{{ conversationStatus.isProcessing ? "正在处理" : "空闲" }}<span v-if="conversationStatus.queuedCount">，{{ conversationStatus.queuedCount }} 个等待中</span></dd></div>
          </dl>
        </section>
        <section v-if="queuedTasks.length" class="queued-task-panel" aria-live="polite">
          <div class="queued-task-heading"><span>待执行</span><strong>{{ queuedTasks.length }}</strong></div>
          <ol>
            <li v-for="task in queuedTasks.slice(0, 3)" :key="task.requestId" :title="task.message"><span>{{ task.position }}</span><p>{{ task.message }}</p><button class="queued-task-remove" type="button" :disabled="task.isRemoving" title="删除待执行任务" @click="removeQueuedTask(task)"><X :size="13" /></button></li>
          </ol>
          <div v-if="queuedTasks.length > 3" class="queued-task-more">另有 {{ queuedTasks.length - 3 }} 个任务</div>
        </section>
        <div class="composer-main">
          <textarea ref="composerInput" v-model="prompt" :disabled="!initialized" rows="1" placeholder="输入任务，Enter 发送，Shift+Enter 换行" @input="promptPaletteIndex = 0" @keydown="handleComposerKeydown"></textarea>
        </div>
        <div class="composer-footer">
          <div class="composer-actions">
            <button class="model-trigger" type="button" :title="`当前模型：${model}`" @click="modelMenuOpen = !modelMenuOpen">{{ model || "配置模型" }}</button>
            <button v-if="isStreaming && !prompt.trim()" class="stop-send-button" type="button" title="停止当前任务" @click="cancelChat">■</button>
            <button v-else type="submit" :disabled="!initialized || !prompt.trim()" :title="isStreaming ? '加入任务队列' : '发送'">↑</button>
          </div>
          <div class="composer-approval-control">
          <button class="approval-mode-trigger" type="button" :title="`当前审批模式：${approvalModeLabel}`" @click="approvalMenuOpen = !approvalMenuOpen"><ShieldCheck v-if="approvalMode !== 'ask'" :size="14" /><Shield v-else :size="14" /><span>{{ approvalModeLabel }}</span></button>
          <section v-if="approvalMenuOpen" class="approval-mode-menu">
            <button type="button" :class="{ active: approvalMode === 'ask' }" @click="setApprovalMode('ask')"><Shield :size="16" /><span><strong>请求批准</strong><small>写入文件或运行脚本前请求确认</small></span><Check v-if="approvalMode === 'ask'" :size="16" /></button>
            <button type="button" :class="{ active: approvalMode === 'write' }" @click="setApprovalMode('write')"><ShieldCheck :size="16" /><span><strong>自动批准文件操作</strong><small>仅自动允许写入工作区文件</small></span><Check v-if="approvalMode === 'write'" :size="16" /></button>
            <button type="button" :class="{ active: approvalMode === 'all' }" @click="setApprovalMode('all')"><ShieldCheck :size="16" /><span><strong>自动批准所有工具</strong><small>后续操作不再重复确认</small></span><Check v-if="approvalMode === 'all'" :size="16" /></button>
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

    <div v-if="deleteTarget" class="dialog-backdrop" @click.self="cancelDelete">
      <section class="delete-dialog" role="dialog" aria-modal="true" aria-labelledby="delete-dialog-title">
        <h2 id="delete-dialog-title">删除会话？</h2>
        <p>“{{ deleteTarget.title || deleteTarget.conversationId }}”及其中的消息将被永久删除，此操作无法恢复。</p>
        <div class="dialog-actions"><button class="dialog-cancel" type="button" @click="cancelDelete">取消</button><button class="dialog-delete" type="button" @click="confirmDelete">删除</button></div>
      </section>
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

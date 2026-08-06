# Agent.Host Protocol

`Agent.Host` is a local backend process for a desktop frontend. It reads one JSON request per line from standard input and writes one JSON event per line to standard output. Do not write diagnostic output to standard output.

Start the process from Electron with the API configuration in its environment, or send it in the first `initialize` request:

```json
{"id":"1","type":"initialize","payload":{"baseUrl":"https://api.example.com/v1","model":"example-model","apiKey":"..."}}
```

For production, pass `AGENT_API_KEY`, `AGENT_BASE_URL`, and `AGENT_MODEL` through the Electron main process instead of sending the API key in a renderer-originated message.

## Requests

```json
{"id":"2","type":"new_conversation","payload":{}}
{"id":"3","type":"list_conversations","payload":{}}
{"id":"4","type":"open_conversation","payload":{"conversationId":"20260806-101530123"}}
{"id":"5","type":"set_workspace","payload":{"conversationId":"20260806-101530123","workspaceRoot":"D:\\StudySpace\\MyAgent"}}
{"id":"6","type":"chat","payload":{"conversationId":"20260806-101530123","message":"Read the project structure."}}
```

`chat` accepts an omitted `conversationId`; in that case the host creates a new conversation ID automatically.

## Events

All events retain the original request `id`, allowing the Electron main process to route concurrent UI requests. Current event types are:

- `initialized`
- `conversation_created`
- `conversation_opened`
- `conversation_list`
- `workspace_changed`
- `chat_started`
- `text_delta` for each streamed text segment
- `completed`
- `error`

The coding-agent prompt emits ReAct-safe visible progress as text segments prefixed with `[Plan]`, `[Observation]`, and `[Answer]`. These are presentation events, not private model reasoning.

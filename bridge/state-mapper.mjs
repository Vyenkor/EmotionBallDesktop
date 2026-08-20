export const EMOTIONS = Object.freeze({
  idle: { emotionId: '04', label: '待机放空' },
  receiving: { emotionId: '31', label: '接收任务' },
  thinking: { emotionId: '30', label: '思考中' },
  busy: { emotionId: '32', label: '处理中忙碌' },
  done: { emotionId: '33', label: '任务完成' },
  error: { emotionId: '34', label: '出错' },
  waiting: { emotionId: '35', label: '等待输入' },
  loading: { emotionId: '36', label: '联网加载' },
  recalling: { emotionId: '37', label: '复述回忆' },
  replying: { emotionId: '39', label: '输出回复' },
  searching: { emotionId: '40', label: '检索资料' },
  stopped: { emotionId: '41', label: '停止终止' }
});

const TOOL_ERROR_RE = /(?:script failed|isError["']?\s*:\s*true|"success"\s*:\s*false|"exit_code"\s*:\s*[1-9]\d*|\bfailed to run\b)/i;
const WAIT_RE = /(?:request_user_input|approval|wait_threads|wait_agent|functions\.wait\b|tools\.wait\b)/i;
const SEARCH_RE = /(?:web__run|web_search|search_query|image_query|\bsearch\b)/i;
const NETWORK_RE = /(?:download|fetch\(|invoke-webrequest|curl\b|wget\b)/i;

function timestampOf(record, fallback = Date.now()) {
  const parsed = Date.parse(record?.timestamp || '');
  return Number.isFinite(parsed) ? parsed : fallback;
}

function safeText(value, limit = 16000) {
  if (typeof value === 'string') return value.slice(0, limit);
  try {
    return JSON.stringify(value).slice(0, limit);
  } catch {
    return '';
  }
}

function classifyTool(payload) {
  const text = [payload?.name, payload?.type, safeText(payload?.input, 12000)].join(' ');
  if (WAIT_RE.test(text)) return 'waiting';
  if (SEARCH_RE.test(text)) return 'searching';
  if (NETWORK_RE.test(text)) return 'loading';
  return 'busy';
}

function outputFailed(payload) {
  return TOOL_ERROR_RE.test(safeText(payload?.output ?? payload?.result ?? payload));
}

export class CodexStateTracker {
  constructor(onChange = () => {}) {
    this.onChange = onChange;
    this.sequence = 0;
    this.turnActive = false;
    this.expiresAt = null;
    this.current = this.#makeState('idle', 'startup', Date.now(), 'high');
  }

  #makeState(key, source, at, confidence, tips) {
    const emotion = EMOTIONS[key] || EMOTIONS.idle;
    return {
      sequence: this.sequence,
      timestamp: new Date(at).toISOString(),
      codexState: key,
      emotionId: emotion.emotionId,
      label: emotion.label,
      tips: tips || emotion.label,
      source,
      confidence
    };
  }

  #set(key, source, at, { confidence = 'high', holdMs = null, tips = null } = {}) {
    const next = this.#makeState(key, source, at, confidence, tips);
    const same = this.current.codexState === next.codexState && this.current.source === next.source;
    this.expiresAt = holdMs ? at + holdMs : null;
    if (same) return false;
    this.sequence += 1;
    next.sequence = this.sequence;
    this.current = next;
    this.onChange(next);
    return true;
  }

  tick(now = Date.now()) {
    if (!this.expiresAt || now < this.expiresAt) return false;
    this.expiresAt = null;
    return this.#set(
      this.turnActive ? 'thinking' : 'idle',
      'timer',
      now,
      { confidence: 'medium' }
    );
  }

  process(record, now = Date.now()) {
    if (!record || typeof record !== 'object') return false;
    const at = timestampOf(record, now);
    const payload = record.payload || {};

    if (record.type === 'event_msg') {
      switch (payload.type) {
        case 'task_started':
          this.turnActive = true;
          return this.#set('receiving', 'task_started', at, { holdMs: 1200 });
        case 'user_message':
          this.turnActive = true;
          return this.#set('receiving', 'user_message', at, { holdMs: 1200 });
        case 'agent_reasoning':
          return this.#set('thinking', 'agent_reasoning', at);
        case 'agent_message':
          return this.#set('replying', `agent_message:${payload.phase || 'unknown'}`, at);
        case 'web_search_end':
          return this.#set('searching', 'web_search_end', at, { holdMs: 1000 });
        case 'context_compacted':
          return this.#set('recalling', 'context_compacted', at, { holdMs: 2200 });
        case 'patch_apply_end':
          return payload.success === false
            ? this.#set('error', 'patch_apply_end', at, { holdMs: 5000 })
            : this.#set('busy', 'patch_apply_end', at, { holdMs: 900 });
        case 'mcp_tool_call_end':
        case 'image_generation_end':
          return outputFailed(payload)
            ? this.#set('error', payload.type, at, { holdMs: 5000 })
            : this.#set('busy', payload.type, at, { holdMs: 900 });
        case 'turn_aborted':
        case 'thread_rolled_back':
          this.turnActive = false;
          return this.#set('stopped', payload.type, at, { holdMs: 5000 });
        case 'task_complete':
          this.turnActive = false;
          return this.#set('done', 'task_complete', at, { holdMs: 6000 });
        default:
          return false;
      }
    }

    if (record.type !== 'response_item') return false;

    if (payload.type === 'message') {
      if (payload.role === 'user') {
        this.turnActive = true;
        return this.#set('receiving', 'message:user', at, { holdMs: 1200 });
      }
      if (payload.role === 'assistant') {
        return this.#set('replying', `message:${payload.phase || 'assistant'}`, at);
      }
      return false;
    }

    if (payload.type === 'reasoning') {
      return this.#set('thinking', 'reasoning', at);
    }

    if (['custom_tool_call', 'function_call', 'web_search_call', 'tool_search_call'].includes(payload.type)) {
      const state = payload.type === 'web_search_call' ? 'searching' : classifyTool(payload);
      return this.#set(state, `${payload.type}:${payload.name || 'unnamed'}`, at);
    }

    if (['custom_tool_call_output', 'function_call_output', 'tool_search_output'].includes(payload.type)) {
      if (outputFailed(payload)) {
        return this.#set('error', payload.type, at, { holdMs: 5000 });
      }
    }

    return false;
  }
}

export function extractThreadId(filePath) {
  const match = String(filePath).match(/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.jsonl$/i);
  return match ? match[1] : null;
}

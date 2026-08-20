import assert from 'node:assert/strict';
import test from 'node:test';
import { CodexStateTracker, extractThreadId } from './state-mapper.mjs';

const at = '2026-08-20T01:00:00.000Z';
const event = (type, extra = {}) => ({ type: 'event_msg', timestamp: at, payload: { type, ...extra } });
const item = (type, extra = {}) => ({ type: 'response_item', timestamp: at, payload: { type, ...extra } });

test('maps the core Codex lifecycle', () => {
  const tracker = new CodexStateTracker();
  tracker.process(event('task_started'));
  assert.equal(tracker.current.emotionId, '31');
  tracker.process(event('agent_reasoning'));
  assert.equal(tracker.current.emotionId, '30');
  tracker.process(event('agent_message', { phase: 'commentary' }));
  assert.equal(tracker.current.emotionId, '39');
  tracker.process(event('task_complete'));
  assert.equal(tracker.current.emotionId, '33');
  tracker.tick(Date.parse(at) + 7000);
  assert.equal(tracker.current.emotionId, '04');
});

test('classifies tools without exposing their arguments', () => {
  const tracker = new CodexStateTracker();
  tracker.process(event('task_started'));
  tracker.process(item('custom_tool_call', { name: 'exec', input: 'await tools.web__run({search_query:[{q:"x"}]})' }));
  assert.equal(tracker.current.emotionId, '40');
  tracker.process(item('custom_tool_call', { name: 'exec', input: 'await tools.request_user_input({})' }));
  assert.equal(tracker.current.emotionId, '35');
  tracker.process(item('function_call', { name: 'exec_command', input: '{}' }));
  assert.equal(tracker.current.emotionId, '32');
});

test('maps failures, compaction, and interruption', () => {
  const tracker = new CodexStateTracker();
  tracker.process(item('function_call_output', { output: '{"exit_code":2}' }));
  assert.equal(tracker.current.emotionId, '34');
  tracker.process(event('context_compacted'));
  assert.equal(tracker.current.emotionId, '37');
  tracker.process(event('turn_aborted'));
  assert.equal(tracker.current.emotionId, '41');
});

test('extracts thread id from rollout filename', () => {
  assert.equal(
    extractThreadId('rollout-2026-08-20T09-01-30-01a01caf-fc05-71d0-ae41-acad1c6a3306.jsonl'),
    '01a01caf-fc05-71d0-ae41-acad1c6a3306'
  );
});

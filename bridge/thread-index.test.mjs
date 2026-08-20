import assert from 'node:assert/strict';
import test from 'node:test';
import { findThreadName } from './thread-index.mjs';

test('reads only the matching thread title and prefers the latest entry', () => {
  const content = [
    JSON.stringify({ id: 'other', thread_name: '其他任务' }),
    JSON.stringify({ id: 'target', thread_name: '  旧标题  ' }),
    '{malformed',
    JSON.stringify({ id: 'target', thread_name: '评估 emotion-ball   宠物集成' })
  ].join('\n');
  assert.equal(findThreadName(content, 'target'), '评估 emotion-ball 宠物集成');
  assert.equal(findThreadName(content, 'missing'), null);
});

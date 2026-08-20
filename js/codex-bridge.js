(function () {
  'use strict';

  var ballEl = document.getElementById('codexBall');
  var statusName = document.getElementById('statusName');
  var statusTips = document.getElementById('statusTips');
  var threadInfo = document.getElementById('threadInfo');
  var connectionDot = document.getElementById('connectionDot');
  var lastSequence = -1;

  var ball = window.EmotionBall.create(ballEl, {
    emotion: '04',
    shape: 'blob',
    idle: false,
    autostart: true,
    lite: false,
    eyeScale: 1.08,
    label: 'Codex 状态宠物'
  });

  function setOnline(online) {
    connectionDot.classList.toggle('online', online);
    connectionDot.title = online ? '已连接本地 Codex 桥接' : '正在重连本地 Codex 桥接';
  }

  function applyStatus(data) {
    if (!data || !data.emotionId) return;
    if (typeof data.sequence === 'number' && data.sequence === lastSequence) return;
    lastSequence = data.sequence;
    ball.handleAIMessage({ emotionId: data.emotionId, tips: data.tips || data.label });
    statusName.textContent = data.label || data.codexState || '未知状态';
    statusTips.textContent = data.tips || '已同步 Codex 状态';
    threadInfo.textContent = data.threadId
      ? '任务 ' + data.threadId.slice(0, 8) + '… · ' + (data.source || 'event')
      : '等待 Codex 任务事件';
    document.body.dataset.codexState = data.codexState || 'unknown';
  }

  function connect() {
    var events = new EventSource('/api/events');
    events.addEventListener('status', function (event) {
      setOnline(true);
      try { applyStatus(JSON.parse(event.data)); }
      catch (error) { console.warn('[EmotionBall Codex] invalid status payload', error); }
    });
    events.onopen = function () { setOnline(true); };
    events.onerror = function () { setOnline(false); };
  }

  ball.on('tips', function (event) {
    if (event && event.text) statusTips.textContent = event.text;
  });

  window.addEventListener('pointermove', function (event) {
    var rect = ballEl.getBoundingClientRect();
    var cx = rect.left + rect.width / 2;
    var cy = rect.top + rect.height / 2;
    ball.setGaze(
      Math.max(-1, Math.min(1, (event.clientX - cx) / 300)),
      Math.max(-1, Math.min(1, (event.clientY - cy) / 300))
    );
  }, { passive: true });

  fetch('/api/status', { cache: 'no-store' })
    .then(function (response) { return response.json(); })
    .then(applyStatus)
    .catch(function () { setOnline(false); })
    .finally(connect);
})();

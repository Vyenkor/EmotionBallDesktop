(function () {
  'use strict';

  var surface = document.getElementById('petSurface');
  var ballElement = document.getElementById('desktopBall');
  var leftPointerDown = false;
  var currentLabel = '正在连接…';
  var currentEmotionId = '04';
  var currentShape = 'blob';
  var currentTaskName = '';
  var currentTaskActive = false;
  var dragBounceTimer = 0;
  var ball = null;
  var hostGazeAvailable = false;

  function createBall(shape) {
    currentShape = ['blob', 'wedge', 'gem'].indexOf(shape) >= 0 ? shape : 'blob';
    if (ball) ball.destroy();
    ballElement.replaceChildren();
    ball = window.EmotionBall.create(ballElement, {
      emotion: currentEmotionId,
      shape: currentShape,
      idle: false,
      autostart: true,
      lite: false,
      eyeScale: 1.1,
      label: 'Codex 桌面宠物'
    });
  }

  createBall(currentShape);

  function postToHost(type, detail) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(Object.assign({ type: type }, detail || {}));
    }
  }

  function setDragging(active) {
    document.body.classList.toggle('dragging', active);
    if (active) {
      if (dragBounceTimer) return;
      ball.bounce();
      dragBounceTimer = window.setInterval(function () {
        ball.bounce();
      }, 120);
      return;
    }
    if (dragBounceTimer) window.clearInterval(dragBounceTimer);
    dragBounceTimer = 0;
  }

  function setOnline(online) {
    if (!online) currentLabel = '正在重连 Codex…';
    postToHost('status', {
      label: currentLabel,
      online: online,
      taskName: currentTaskName,
      taskActive: currentTaskActive
    });
  }

  function applyStatus(data) {
    if (!data || !data.emotionId) return;
    currentEmotionId = data.emotionId;
    currentTaskName = typeof data.taskName === 'string' ? data.taskName : '';
    currentTaskActive = Boolean(data.taskActive);
    ball.handleAIMessage({ emotionId: data.emotionId, tips: data.label });
    currentLabel = data.label || data.codexState || '未知状态';
    document.body.dataset.codexState = data.codexState || 'unknown';
    postToHost('status', {
      label: currentLabel,
      online: true,
      taskName: currentTaskName,
      taskActive: currentTaskActive
    });
  }

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (event) {
      var data = event.data;
      if (!data) return;
      if (data.type === 'set-shape') {
        createBall(data.shape);
      } else if (data.type === 'set-local-emotion' && data.emotionId) {
        currentEmotionId = data.emotionId;
        ball.handleAIMessage({
          emotionId: data.emotionId,
          tips: data.tips || ''
        });
      } else if (data.type === 'set-gaze') {
        hostGazeAvailable = true;
        if (data.active) {
          ball.setGaze(
            typeof data.x === 'number' ? data.x : 0,
            typeof data.y === 'number' ? data.y : 0
          );
        } else {
          ball.clearGaze();
        }
      } else if (data.type === 'set-dragging') {
        setDragging(Boolean(data.active));
      } else if (data.type === 'set-hover-dimmed') {
        if (typeof data.opacity === 'number') {
          document.body.style.setProperty('--hover-opacity', String(data.opacity));
        }
        document.body.classList.toggle('hover-dimmed', Boolean(data.dimmed));
      }
    });
  }

  surface.addEventListener('pointerdown', function (event) {
    if (event.button !== 0) return;
    leftPointerDown = true;
    setDragging(true);
    postToHost('drag');
  });
  window.addEventListener('pointerup', function () {
    leftPointerDown = false;
    setDragging(false);
  });
  window.addEventListener('pointercancel', function () {
    leftPointerDown = false;
    setDragging(false);
  });
  surface.addEventListener('wheel', function (event) {
    if (!leftPointerDown) return;
    event.preventDefault();
    postToHost('resize-step', { delta: event.deltaY < 0 ? 1 : -1 });
  }, { passive: false });
  surface.addEventListener('contextmenu', function (event) {
    event.preventDefault();
    postToHost('menu');
  });
  surface.addEventListener('dblclick', function () {
    postToHost('toggle-topmost');
  });
  window.addEventListener('keydown', function (event) {
    if (event.key === 'Escape') postToHost('close');
  });
  window.addEventListener('pointermove', function (event) {
    // The native transparent input overlay normally owns pointer events. Keep
    // this as a browser-preview fallback, but let the host-level gaze stream
    // win when the packaged desktop pet is running.
    if (hostGazeAvailable) return;
    var rect = ballElement.getBoundingClientRect();
    var cx = rect.left + rect.width / 2;
    var cy = rect.top + rect.height / 2;
    var dx = event.clientX - cx;
    var dy = event.clientY - cy;
    var halfWidth = Math.max(1, window.innerWidth / 2);
    var halfHeight = Math.max(1, window.innerHeight / 2);
    ball.setGaze(
      Math.max(-1, Math.min(1, dx / halfWidth)),
      Math.max(-1, Math.min(1, dy / halfHeight))
    );
  }, { passive: true });

  function connect() {
    var source = new EventSource('/api/events');
    source.addEventListener('status', function (event) {
      setOnline(true);
      try { applyStatus(JSON.parse(event.data)); }
      catch (error) { console.warn('[Emotionball-Deskpet] invalid status payload', error); }
    });
    source.onopen = function () { setOnline(true); };
    source.onerror = function () { setOnline(false); };
  }

  fetch('/api/status', { cache: 'no-store' })
    .then(function (response) { return response.json(); })
    .then(applyStatus)
    .catch(function () { setOnline(false); })
    .finally(connect);
})();

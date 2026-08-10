declare global {
  interface Window {
    AndroidBridge?: {
      createSession: (tabId: number) => void;
      closeSession?: (tabId: number) => void;
      invokeCommand: (tabId: number, cmd: string) => void;
      sendInput: (tabId: number, json: string) => void;
      notifyReady: () => void;
      minimizeApp?: () => void;
      startProjection?: () => void;
      exitApp?: () => void;
      getScripts?: () => string;
    };
    chrome?: { webview?: { postMessage: (msg: unknown) => void }; };
  }
}

type OutputCallback = (text: string) => void;
const listeners: Record<number, OutputCallback[]> = {};
let ws: WebSocket | null = null;

if (typeof window !== 'undefined') {
  window.addEventListener('message', (e: MessageEvent) => {
    if (typeof e.data === 'string') {
      const idx = e.data.indexOf(':');
      if (idx > -1) {
          const tabId = parseInt(e.data.substring(0, idx));
          const text = e.data.substring(idx + 1);
          if (listeners[tabId]) listeners[tabId].forEach(l => l(text));
      } else {
          if (listeners[1]) listeners[1].forEach(l => l(e.data));
      }
    }
  });

  if (!window.AndroidBridge && window.location.protocol.startsWith('http')) {
    ws = new WebSocket(`ws://${window.location.host}/`);
    ws.onmessage = (e) => {
      if (typeof e.data !== 'string') return;
      const idx = e.data.indexOf(':');
      if (idx > -1) {
        const tabId = parseInt(e.data.substring(0, idx));
        const text = e.data.substring(idx + 1);
        if (listeners[tabId]) listeners[tabId].forEach(l => l(text));
      } else if (listeners[1]) {
        listeners[1].forEach(l => l(e.data));
      }
    };
  }
}

export const subscribeToOutput = (tabId: number, callback: OutputCallback): (() => void) => {
  if (!listeners[tabId]) listeners[tabId] = [];
  listeners[tabId].push(callback);
  return () => {
    const i = listeners[tabId].indexOf(callback);
    if (i > -1) listeners[tabId].splice(i, 1);
  };
};

export const createSession = (tabId: number): void => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge?.createSession) bridge.createSession(tabId);
  else if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'createSession', tabId }));
  } else if (ws) {
    ws.addEventListener('open', () => {
      ws.send(JSON.stringify({ type: 'createSession', tabId }));
    }, { once: true });
  }
};

export const closeSession = (tabId: number): void => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge?.closeSession) bridge.closeSession(tabId);
  else if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'closeSession', tabId }));
  }
};

export const exitApp = (): void => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge?.exitApp) bridge.exitApp();
};

export const notifyReady = (): void => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge?.notifyReady) bridge.notifyReady();
};

export const minimizeApp = (): void => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge?.minimizeApp) bridge.minimizeApp();
};

export const startProjection = (): void => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge?.startProjection) bridge.startProjection();
};

export const sendInput = (tabId: number, data: string): void => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  
  const sendPayload = (payload: string) => {
    if (bridge) bridge.sendInput(tabId, payload);
    else if (ws && ws.readyState === WebSocket.OPEN) ws.send(payload);
    else window.chrome?.webview?.postMessage(JSON.parse(payload));
  };

  if (data === '\x1b[A') { sendPayload(JSON.stringify({ type: 'input', tabId, key: 'ArrowUp' }));    return; }
  if (data === '\x1b[B') { sendPayload(JSON.stringify({ type: 'input', tabId, key: 'ArrowDown' }));  return; }
  if (data === '\x1b[C') { sendPayload(JSON.stringify({ type: 'input', tabId, key: 'ArrowRight' })); return; }
  if (data === '\x1b[D') { sendPayload(JSON.stringify({ type: 'input', tabId, key: 'ArrowLeft' }));  return; }

  if (data === '\r')           { sendPayload(JSON.stringify({ type: 'input', tabId, key: 'Enter' }));     return; }
  if (data === '\x7F' || data === '\b') { sendPayload(JSON.stringify({ type: 'input', tabId, key: 'Backspace' })); return; }
  if (data === '\t')           { sendPayload(JSON.stringify({ type: 'input', tabId, key: 'Tab' }));       return; }
  if (data === '\x1b')         { sendPayload(JSON.stringify({ type: 'input', tabId, key: 'Escape' }));    return; }

  sendPayload(JSON.stringify({ type: 'text', tabId, text: data }));
};

export const sendResize = (tabId: number, cols: number, rows: number): void => {
  const payload = JSON.stringify({ type: 'resize', tabId, cols, rows });
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge) bridge.sendInput(tabId, payload);
  else if (ws && ws.readyState === WebSocket.OPEN) ws.send(payload);
  else window.chrome?.webview?.postMessage({ type: 'resize', cols, rows });
};

export const invokeCommand = (tabId: number, cmd: string): void => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge?.invokeCommand) bridge.invokeCommand(tabId, cmd);
};

export const getScripts = (): string[] => {
  const bridge = typeof window !== 'undefined' ? window.AndroidBridge : undefined;
  if (bridge?.getScripts) {
    try {
      return JSON.parse(bridge.getScripts());
    } catch {
      return [];
    }
  }
  return [];
};

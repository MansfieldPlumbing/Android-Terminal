import React, { useEffect, useRef } from 'react';
import { useAppStore, Tab } from '../System.Store';

const PALETTE = [
  '#0C0C0C', '#C50F1F', '#13A10E', '#C19C00', '#0037DA', '#881798', '#3A96DD', '#CCCCCC',
  '#767676', '#E74856', '#16C60C', '#F9F1A5', '#3B78FF', '#B4009E', '#61D6D6', '#F2F2F2'
];

export default function TerminalCanvas({ tab }: { tab: Tab }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const currentBounds = useRef({ cols: 120, rows: 40 });
    const [offline, setOffline] = React.useState(false);
  const [offlineStatus, setOfflineStatus] = React.useState('ERR_CONNECTION_REFUSED');
  const [reconnectAttempts, setReconnectAttempts] = React.useState(0);
  const addTab = useAppStore(state => state.addTab);
  const CELL_W = 9;
  const CELL_H = 18;
  const FONT_SIZE = 14;

  useEffect(() => {
    const preventZoom = (e: WheelEvent) => { if (e.ctrlKey || e.metaKey) e.preventDefault(); };
    window.addEventListener('wheel', preventZoom, { passive: false });
    return () => window.removeEventListener('wheel', preventZoom);
  }, []);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;
    const ctx = canvas.getContext('2d', { alpha: false });
    if (!ctx) return;

    // Set focus to the container instantly so it accepts keystrokes
    container.focus();

    const resizeObserver = new ResizeObserver((entries) => {
      const { width, height } = entries[0].contentRect;
      const newCols = Math.max(1, Math.floor(width / CELL_W));
      const newRows = Math.max(1, Math.floor(height / CELL_H));
      const dpr = window.devicePixelRatio || 1;

      canvas.width = Math.floor(width * dpr);
      canvas.height = Math.floor(height * dpr);

      if (currentBounds.current.cols !== newCols || currentBounds.current.rows !== newRows) {
        currentBounds.current = { cols: newCols, rows: newRows };
        if (typeof window !== 'undefined' && (window as any).AndroidBridge?.sendInput) {
            (window as any).AndroidBridge.sendInput(JSON.stringify({ type: 'resize', cols: newCols, rows: newRows }));
        }
      }
    });
    resizeObserver.observe(container);

    const onMessage = (e: MessageEvent) => {
      if (!(e.data instanceof ArrayBuffer)) return;
      const grid = new Uint32Array(e.data);
      if (grid.length < 2) return;
      
      const activeCols = grid[0];
      const activeRows = grid[1];

      const cw = canvas.width / (window.devicePixelRatio || 1);
      const ch = canvas.height / (window.devicePixelRatio || 1);

      ctx.save();
      ctx.scale(window.devicePixelRatio || 1, window.devicePixelRatio || 1);
      ctx.scale(cw / (activeCols * CELL_W), ch / (activeRows * CELL_H));

      ctx.fillStyle = PALETTE[0];
      ctx.fillRect(0, 0, activeCols * CELL_W, activeRows * CELL_H);
      ctx.font = `bold ${FONT_SIZE}px "Cascadia Code", Consolas, monospace`;
      ctx.textBaseline = 'top';

      for (let i = 0; i < activeCols * activeRows; i++) {
        // Bounds check
        if (i + 2 >= grid.length) break;
        
        const cell = grid[i + 2];
        if (cell === 0) continue;

        const charCode = cell & 0xFFFF;
        const fg = (cell >> 16) & 0xFF;
        const bg = (cell >> 24) & 0xFF;
        const x = (i % activeCols) * CELL_W;
        const y = Math.floor(i / activeCols) * CELL_H;

        if (bg !== 0) {
          ctx.fillStyle = PALETTE[bg % 16];
          ctx.fillRect(x, y, CELL_W + 0.5, CELL_H + 0.5);
        }
        if (charCode !== 32 && charCode !== 0) {
          ctx.fillStyle = PALETTE[fg % 16];
          ctx.fillText(String.fromCharCode(charCode), x, y + 1);
        }
      }
      ctx.restore();
    };

    let isLocalDev = false;
    if (typeof window !== 'undefined' && !(window as any).AndroidBridge) {
       isLocalDev = true;
       setOffline(true);
    }

    // Watchdog: if bridge exists but no frame arrives within 5s, show offline screen
    let __offlineWatchdog: ReturnType<typeof setTimeout> | null = null;
    if (!isLocalDev) {
      __offlineWatchdog = setTimeout(() => {
        setOffline(prev => {
          if (prev) return prev; // already offline
          setOfflineStatus('ERR_NO_FRAMES (watchdog)');
          return true;
        });
      }, 5000);
    }

    const handleMessage = (e: MessageEvent) => {
      if (__offlineWatchdog !== null) { clearTimeout(__offlineWatchdog); __offlineWatchdog = null; }
      setOffline((prev) => {
        if (prev) return false;
        return prev;
      });
      onMessage(e);
    };

    window.addEventListener('message', handleMessage);
    return () => { 
      window.removeEventListener('message', handleMessage); 
      resizeObserver.disconnect(); 
    };
  }, []);

  const handleTouch = (action: string) => (e: React.PointerEvent) => {
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect) return;
    const c = currentBounds.current.cols;
    const r = currentBounds.current.rows;
    const col = Math.floor((e.clientX - rect.left) / (rect.width / c));
    const row = Math.floor((e.clientY - rect.top) / (rect.height / r));

    if (col >= 0 && col < c && row >= 0 && row < r) {
      if (typeof window !== 'undefined' && (window as any).AndroidBridge?.sendInput) {
        (window as any).AndroidBridge.sendInput(JSON.stringify({ type: 'touch', action, col, row }));
      }
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      const val = e.currentTarget.value;
      if (typeof window !== 'undefined' && (window as any).AndroidBridge?.invokeCommand) {
          (window as any).AndroidBridge.invokeCommand(val);
      }
      e.currentTarget.value = '';
    }
  };

  return (
    <div
      ref={containerRef}
      className="absolute inset-0 bg-transparent overflow-hidden outline-none flex flex-col"
    >
      <div className="flex-1 relative outline-none" tabIndex={0}>
        <canvas ref={canvasRef} className="w-full h-full touch-none"
          onPointerDown={handleTouch('down')} onPointerUp={handleTouch('up')} onPointerMove={handleTouch('move')}
        />
        {offline && (
          <div className="absolute inset-0 z-50 bg-[#252525] flex flex-col justify-center px-12 md:px-24 select-text">
            <div className="text-[120px] leading-none font-light text-blue-400 mb-8 tracking-tighter">
              :(
            </div>
            <div className="text-xl md:text-2xl text-white font-medium max-w-2xl leading-relaxed">
              Backend appears to be offline.
              <div className="text-sm font-mono text-gray-400 mt-2 mb-2">
                 HTTP/1.1 503 Service Unavailable
                 <br/>
                 ErrorCode: {offlineStatus}
                 {reconnectAttempts > 0 && <span className="ml-4 text-blue-400">Reconnect attempt {reconnectAttempts}/3...</span>}
              </div>
              <br className="mb-4" />
              <div className="flex gap-4">
                <a href="#" onClick={(e) => { 
                  e.preventDefault(); 
                  if (reconnectAttempts < 3) {
                     setOfflineStatus('WSAECONNREFUSED (10061)');
                     setReconnectAttempts(prev => prev + 1);
                     setTimeout(() => {
                        if (reconnectAttempts >= 2) {
                           setOfflineStatus('ERR_HOST_UNREACHABLE');
                        }
                     }, 1000);
                  } 
                }} className="mt-4 inline-block text-blue-400 hover:text-blue-300 underline font-medium">
                  Try again
                </a>
                <a href="#" onClick={(e) => { e.preventDefault(); addTab({ id: 'debug-'+Date.now(), type: 'debug', title: 'Debug Session' }); }} className="mt-4 inline-block text-gray-400 hover:text-gray-300 underline font-medium">
                  Open Debug Menu
                </a>
              </div>
            </div>
          </div>
        )}
      </div>
      {/* Local Buffer Input Box */}
      <div className="h-12 bg-[#1e1e1e] border-t border-[#333] flex items-center px-2 shrink-0 z-40 relative">
         <span className="text-pink-500 font-bold mr-2 text-sm select-none">&gt;_</span>
         <input 
           type="text" 
           className="flex-1 bg-transparent border-none outline-none text-gray-200 font-mono text-sm h-full"
           placeholder="Enter PowerShell command..."
           onKeyDown={handleKeyDown}
           autoComplete="off"
           autoCorrect="off"
           autoCapitalize="off"
           spellCheck="false"
         />
      </div>
    </div>
  );
}

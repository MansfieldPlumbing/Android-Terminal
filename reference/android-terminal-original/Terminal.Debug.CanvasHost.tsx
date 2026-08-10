import React, { useEffect, useRef, useState } from 'react';
import { useAppStore, Tab } from '../System.Store';
import { Settings2, Activity, Palette, Type, Hash, MousePointer2, Move, ChevronRight } from 'lucide-react';
import { cn } from '../System.Utils';
import { motion, AnimatePresence } from 'motion/react';
import { WinSlider } from './Terminal.Settings.Themes';

const PALETTE = [
  '#0C0C0C', '#C50F1F', '#13A10E', '#C19C00', '#0037DA', '#881798', '#3A96DD', '#CCCCCC',
  '#767676', '#E74856', '#16C60C', '#F9F1A5', '#3B78FF', '#B4009E', '#61D6D6', '#F2F2F2'
];

type SmokeMode = 'grid' | 'touch' | 'colors' | 'charset' | 'noise' | 'pan';

export default function TerminalDebugView({ tab }: { tab: Tab }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const currentBounds = useRef({ cols: 120, rows: 40 });

  const [smokeMode, setSmokeMode] = useState<SmokeMode>('touch');
  const { canvasDpi: dpi, setCanvasDpi, canvasBlockScale: blockScale, setCanvasBlockScale, testsMenuOpen, setTestsMenuOpen } = useAppStore();
  const [lastStats, setLastStats] = useState({ cols: 0, rows: 0 });
  
  const dim = {
     1: { w: 9, h: 18, font: 14 },
     2: { w: 7, h: 14, font: 11 },
     3: { w: 5, h: 10, font: 8 },
     4: { w: 3, h: 6, font: 5 },
     5: { w: 2, h: 4, font: 4 }
  }[dpi as 1|2|3|4|5] || { w: 9, h: 18, font: 14 };

  const CELL_W = dim.w * blockScale;
  const CELL_H = dim.h * blockScale;
  const FONT_SIZE = dim.font * blockScale;
  
  const simModeRef = useRef(smokeMode);
  const blockScaleRef = useRef(blockScale);
  const dpiRef = useRef(dpi);
  const touchPointsRef = useRef<{c: number, r: number, time: number}[]>([]);
  const panOffsetRef = useRef({ x: 0, y: 0 });
  const isDraggingRef = useRef(false);
  const lastPanTouchRef = useRef({ x: 0, y: 0 });

  const [pingResult, setPingResult] = useState<string | null>(null);

  useEffect(() => {
    simModeRef.current = smokeMode;
    blockScaleRef.current = blockScale;
    dpiRef.current = dpi;
  }, [smokeMode, blockScale, dpi]);

  useEffect(() => {
    const preventZoom = (e: WheelEvent) => { 
      if (e.ctrlKey || e.metaKey) e.preventDefault(); 
      else {
         if (simModeRef.current === 'pan') {
           panOffsetRef.current.x -= e.deltaX / CELL_W;
           panOffsetRef.current.y -= e.deltaY / CELL_H;
         }
      }
    };
    window.addEventListener('wheel', preventZoom, { passive: false });
    return () => window.removeEventListener('wheel', preventZoom);
  }, [CELL_W, CELL_H]);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;
    const ctx = canvas.getContext('2d', { alpha: false });
    if (!ctx) return;

    container.focus();

    const resizeObserver = new ResizeObserver((entries) => {
      const { width, height } = entries[0].contentRect;
      if (width < 32 || height < 32) return;
      const newCols = Math.max(1, Math.floor(width / CELL_W));
      const newRows = Math.max(1, Math.floor(height / CELL_H));
      const dpr = window.devicePixelRatio || 1;

      canvas.width = Math.floor(width * dpr);
      canvas.height = Math.floor(height * dpr);

      currentBounds.current = { cols: newCols, rows: newRows };
    });
    resizeObserver.observe(container);

    let frame = 0;
    const interval = setInterval(() => {
      const c = currentBounds.current.cols;
      const r = currentBounds.current.rows;
      if (c <= 0 || r <= 0) return;
      
      const grid = new Uint32Array((c * r) + 2);
      grid[0] = c; grid[1] = r; 
      
      const mode = simModeRef.current;
      const now = Date.now();
      // Fading touch trails for flinger
      touchPointsRef.current = touchPointsRef.current.filter(t => now - t.time < 500);

      for (let y = 0; y < r; y++) {
        for (let x = 0; x < c; x++) {
           const i = y * c + x;
           let charCode = 0; let fg = 15; let bg = 0;
           
           if (mode === 'grid') {
              if ((x + y) % 2 === 0) { bg = 8; charCode = 32; } 
              if (x === 0 || y === 0 || x === c - 1 || y === r - 1) { bg = 1; fg = 15; charCode = 35; }
              if (x % 10 === 0 && y % 5 === 0) { bg = 4; fg = 15; charCode = 79; }
           } else if (mode === 'colors') {
              bg = Math.floor(x / (Math.max(1, c / 16))) % 16;
              fg = (bg + 8) % 16;
              charCode = 65 + (y % 26);
           } else if (mode === 'charset') {
              charCode = 33 + (((y * c + x) + frame) % 94);
              fg = (y % 15) + 1;
           } else if (mode === 'noise') {
              if (Math.random() > 0.95) { charCode = 33 + Math.floor(Math.random() * 90); fg = Math.floor(Math.random() * 16); }
           } else if (mode === 'pan') {
              const wx = x - Math.floor(panOffsetRef.current.x);
              const wy = y - Math.floor(panOffsetRef.current.y);
              const isEven = (Math.abs(wx) % 2 === 0);
              const isEvenY = (Math.abs(wy) % 2 === 0);
              bg = isEven !== isEvenY ? 0 : 8;
              
              if (wx === 0 && wy === 0) { bg = 4; fg = 15; charCode = 88; } // Origin 0,0
              else if (wx % 10 === 0 && wy % 10 === 0) { charCode = 43; fg = 15; } // Grid intersections
              
              if (x === Math.floor(c/2) && y === Math.floor(r/2)) {
                 bg = 1; fg = 15; charCode = 64; // @
              }
           }

           const touch = touchPointsRef.current.find(t => t.c === x && t.r === y);
           if (touch) {
              const age = now - touch.time;
              if (age < 100) { bg = 14; fg = 0; charCode = 88; } 
              else if (age < 300) { bg = 6; fg = 15; charCode = 120; }  
              else { bg = 8; fg = 15; charCode = 46; } 
           }
           
           // Info Box for Touch Mode
           if (mode === 'touch' && y >= 2 && y <= 6 && x >= 2 && x <= 45) {
              bg = 0; fg = 10; 
              charCode = 32;
              if (y === 3 && x >= 4 && x < 4 + 31) {
                 const txt = " CANVAS HOST: FLINGER DIAGNOSTICS ";
                 if (x - 4 < txt.length) { charCode = txt.charCodeAt(x - 4); fg = 15; bg=4; }
              }
              if (y === 5 && x >= 4 && x < 4 + 35) {
                 const tr = touchPointsRef.current;
                 const txt = tr.length > 0 ? ` COORDS: [X:${tr[tr.length-1].c.toString().padStart(3, '0')} Y:${tr[tr.length-1].r.toString().padStart(3, '0')}] ` : " WAITING FOR INPUT... ";
                 if (x - 4 < txt.length) { charCode = txt.charCodeAt(x - 4); fg = 10; }
              }
           }

           grid[i + 2] = (bg << 24) | (fg << 16) | charCode;
        }
      }

      // Draw grid manually
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
      
      // Update state for last stats 
      if (frame % 20 === 0) {
         setLastStats({ cols: activeCols, rows: activeRows });
      }

      frame++;
    }, 1000 / 60);

    return () => { clearInterval(interval); resizeObserver.disconnect(); };
  }, [CELL_W, CELL_H, FONT_SIZE]);

  const handleTouch = (action: string) => (e: React.PointerEvent) => {
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect) return;
    const c = currentBounds.current.cols;
    const r = currentBounds.current.rows;
    const col = Math.floor((e.clientX - rect.left) / (rect.width / c));
    const row = Math.floor((e.clientY - rect.top) / (rect.height / r));
    
    if (action === 'down') {
       isDraggingRef.current = true;
       lastPanTouchRef.current = { x: e.clientX, y: e.clientY };
    } else if (action === 'move' && isDraggingRef.current && simModeRef.current === 'pan') {
       const dx = e.clientX - lastPanTouchRef.current.x;
       const dy = e.clientY - lastPanTouchRef.current.y;
       panOffsetRef.current.x -= dx / CELL_W;
       panOffsetRef.current.y -= dy / CELL_H;
       lastPanTouchRef.current = { x: e.clientX, y: e.clientY };
    } else if (action === 'up') {
       isDraggingRef.current = false;
    }

    if (col >= 0 && col < c && row >= 0 && row < r) {
      if (action === 'down' || action === 'move') {
        touchPointsRef.current.push({ c: col, r: row, time: Date.now() });
      }
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    e.preventDefault();
  };

  return (
    <div
      ref={containerRef}
      tabIndex={0}
      onKeyDown={handleKeyDown}
      className="absolute inset-0 bg-[#000000] overflow-hidden outline-none flex flex-col font-sans"
    >
      <div className="flex-1 relative overflow-hidden" onClick={() => setTestsMenuOpen(false)}>
        <canvas ref={canvasRef} className="w-full h-full touch-none"
          onPointerDown={handleTouch('down')} onPointerUp={handleTouch('up')} onPointerMove={handleTouch('move')}
        />
        <AnimatePresence>
          {pingResult && (
            <motion.div 
              initial={{ opacity: 0, y: -20 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: -20 }}
              className="absolute top-4 left-1/2 -translate-x-1/2 bg-black/80 border border-white/20 text-white px-4 py-2 rounded shadow-xl font-mono text-sm pointer-events-none z-50 backdrop-blur"
            >
              {pingResult}
            </motion.div>
          )}
        </AnimatePresence>
      </div>
      
      <AnimatePresence>
        {testsMenuOpen && (
          <motion.div 
            initial={{ opacity: 0, y: 10, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 10, scale: 0.98 }}
            transition={{ duration: 0.15 }}
            className="app-dropdown-menu absolute bottom-10 left-0 shadow-2xl w-64 z-30 pointer-events-auto flex flex-col text-sm font-sans text-shadow-mica overflow-hidden mica-panel mica-border rounded-t-xl rounded-br-xl"
          >
            <div className="flex flex-col py-2">
                 <div className="px-5 py-3 flex flex-col gap-4 border-b border-white/10 mb-2">
                    <div className="flex flex-col gap-2">
                      <div className="flex items-center justify-between text-sm font-medium text-gray-300">
                        <span>DPI Scale</span>
                        <span className="font-mono text-gray-400 w-12 text-right">{dpi}</span>
                      </div>
                      <WinSlider 
                        min={1} max={5} step={1} 
                        value={dpi} onChange={setCanvasDpi} 
                      />
                    </div>
                    <div className="flex flex-col gap-2">
                      <div className="flex items-center justify-between text-sm font-medium text-gray-300">
                        <span>Block Scale</span>
                        <span className="font-mono text-gray-400 w-12 text-right">{blockScale}x</span>
                      </div>
                      <WinSlider 
                        min={1} max={8} step={1} 
                        value={blockScale} onChange={setCanvasBlockScale} 
                      />
                    </div>
                 </div>
                 {[
                   { id: 'ping', label: 'Ping Backend' },
                   { id: 'touch', label: 'Touch Event Overlay' },
                   { id: 'pan', label: 'Camera Pan Map' },
                   { id: 'colors', label: '16-Color Palette' },
                   { id: 'charset', label: 'Glyph Cache' },
                   { id: 'grid', label: 'Alignment Grid' },
                   { id: 'noise', label: 'Noise Stress Test' },
                 ].map(m => (
                   <button 
                     key={m.id}
                     onClick={() => { 
                        if (m.id === 'ping') {
                           setPingResult('Pinging backend...');
                           setTimeout(() => setPingResult('Backend is unreachable. Connection refused.'), 1000);
                           setTimeout(() => setPingResult(null), 4000);
                           setTestsMenuOpen(false);
                           return;
                        }
                        setSmokeMode(m.id as SmokeMode); 
                        setTestsMenuOpen(false); 
                     }}
                     className="w-full px-5 py-2.5 active:bg-[rgba(255,255,255,0.15)] active:scale-[0.98] transition-all duration-300 ease-out active:duration-0 flex items-center justify-between text-shadow-mica text-left font-medium"
                   >
                     <span>{m.label}</span>
                     {smokeMode === m.id && <div className="w-1.5 h-1.5 rounded-full bg-blue-500" />}
                   </button>
                 ))}
              </div>

              </motion.div>
        )}
      </AnimatePresence>

      {/* Toolbar */}
      <div className="flex items-center justify-between pointer-events-auto bg-[#2D2D2D] border-t border-[#111111] h-10 px-2 shrink-0 z-20 shadow-[0_-4px_24px_rgba(0,0,0,0.5)]">
        <div className="flex items-center gap-2 h-full">
           <button 
             onClick={() => setTestsMenuOpen(!testsMenuOpen)}
             className={cn("app-menu-trigger h-full px-3 flex items-center justify-center gap-2 hover:bg-white/10 transition-colors rounded-sm", testsMenuOpen ? "bg-white/10" : "text-gray-300")}
           >
             <Settings2 size={16} />
             <span className="font-medium">Tests</span>
           </button>
        </div>
        
        <div className="flex items-center gap-3 px-3">
           <div className="font-sans font-medium text-gray-400">
             {lastStats.cols > 0 && `${lastStats.cols} × ${lastStats.rows}`}
           </div>
        </div>
      </div>
    </div>
  );
}

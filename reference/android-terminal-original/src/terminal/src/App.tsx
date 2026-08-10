import React, { useState, useRef, useEffect, useCallback } from 'react';
import { Plus, X, Minus, Terminal, FileCode2, MonitorPlay, Menu } from 'lucide-react';
import { TerminalEmulator } from './components/TerminalEmulator';
import { sendInput, minimizeApp, startProjection, createSession, exitApp, invokeCommand, closeSession, getScripts } from './lib/pwshBridge';
import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

function cn(...inputs: ClassValue[]) { return twMerge(clsx(inputs)); }

const dispatchKey = (data: string) => window.dispatchEvent(new CustomEvent('key-input', { detail: data }));
const dispatchDPad = (type: string) => window.dispatchEvent(new CustomEvent('dpad-input', { detail: type }));

function Powerstick() {
  const baseRef = useRef<HTMLDivElement>(null);
  const nubRef = useRef<HTMLDivElement>(null);

  const [pos, setPos] = useState({ x: 24, y: 0 });
  const modeRef = useRef<'idle' | 'move' | 'flick'>('idle');
  const startRef = useRef({ x: 0, y: 0 });
  const offsetRef = useRef({ x: 0, y: 0 });
  const lastDragPosRef = useRef({ x: 0, y: 0 });
  const [fancyZone, setFancyZone] = useState<'left' | 'right' | null>(null);
  const [nestledState, setNestledState] = useState<'left' | 'right' | null>(null);
  const nestledRef = useRef<'left' | 'right' | null>(null);
  const intervalRef = useRef<NodeJS.Timeout | null>(null);
  const currentDirRef = useRef<string | null>(null);
  const longPressTimerRef = useRef<NodeJS.Timeout | null>(null);
  const longPressedRef = useRef(false);

  const stopHold = useCallback(() => {
    if (intervalRef.current) clearInterval(intervalRef.current);
    intervalRef.current = null;
    currentDirRef.current = null;
  }, []);

  const startHold = useCallback((dir: string) => {
    if (currentDirRef.current === dir) return;
    stopHold();
    currentDirRef.current = dir;
    
    const action = () => {
      dispatchDPad(dir);
    };
    
    action();
    intervalRef.current = setInterval(action, 100);
  }, [stopHold]);

  const setNestled = useCallback((val: 'left' | 'right' | null) => {
    nestledRef.current = val;
    setNestledState(val);
  }, []);

  useEffect(() => {
    const handleResize = () => {
      setPos(p => {
        // If nestled, keep nestled but adjust X coordinate if nestled right
        if (nestledRef.current === 'left') {
          return { x: -65, y: Math.max(24, Math.min(p.y, window.innerHeight - 154)) };
        }
        if (nestledRef.current === 'right') {
          return { x: window.innerWidth - 65, y: Math.max(24, Math.min(p.y, window.innerHeight - 154)) };
        }

        // Check if joystick went off-screen
        const maxX = window.innerWidth - 130;
        const maxY = window.innerHeight - 130;
        const isOffScreen = p.x > maxX || p.y > maxY || p.x < 0 || p.y < 0;

        if (isOffScreen) {
          // Auto-dock to the nearest edge
          const cx = p.x + 65;
          const leftDist = cx;
          const rightDist = window.innerWidth - cx;
          const targetEdge = leftDist < rightDist ? 'left' : 'right';
          
          setNestled(targetEdge);
          return {
            x: targetEdge === 'left' ? -65 : window.innerWidth - 65,
            y: Math.max(24, Math.min(p.y, window.innerHeight - 154))
          };
        }
        return p;
      });
    };

    window.addEventListener('resize', handleResize);
    setPos({ x: window.innerWidth - 110, y: window.innerHeight - 154 });

    return () => {
      window.removeEventListener('resize', handleResize);
      stopHold();
    };
  }, [stopHold, setNestled]);

  const onPointerDown = useCallback((e: React.PointerEvent) => {
    e.preventDefault();
    e.stopPropagation();
    const el = baseRef.current;
    if (!el) return;
    if (nestledRef.current) {
      setPos(p => ({ x: nestledRef.current === 'left' ? 32 : window.innerWidth - 162, y: p.y }));
      setNestled(null);
    }
    el.setPointerCapture(e.pointerId);
    startRef.current = { x: e.clientX, y: e.clientY };
    lastDragPosRef.current = { x: e.clientX, y: e.clientY };
    longPressedRef.current = false;
    
    if (nubRef.current && nubRef.current.contains(e.target as Node)) {
      modeRef.current = 'flick';
      el.style.transitionProperty = 'background, box-shadow';

      longPressTimerRef.current = setTimeout(() => {
        longPressedRef.current = true;
        if (navigator.vibrate) navigator.vibrate(50);
      }, 400);

    } else {
      modeRef.current = 'move';
      el.style.transitionProperty = 'background, transform, box-shadow';
      el.style.transform = 'scale(1.05)';
      el.style.background = 'rgba(40, 40, 40, 0.5)';
      if (navigator.vibrate) navigator.vibrate(50);
    }
  }, [setNestled]);

  const onPointerMove = useCallback((e: React.PointerEvent) => {
    e.stopPropagation();
    if (longPressTimerRef.current) {
      const dx = e.clientX - startRef.current.x;
      const dy = e.clientY - startRef.current.y;
      if (Math.sqrt(dx*dx + dy*dy) > 10) {
        clearTimeout(longPressTimerRef.current);
        longPressTimerRef.current = null;
      }
    }

    if (nestledRef.current) return;
    if (modeRef.current === 'move') {
      const dx = e.clientX - lastDragPosRef.current.x;
      const dy = e.clientY - lastDragPosRef.current.y;
      lastDragPosRef.current = { x: e.clientX, y: e.clientY };
      setPos(p => {
        const nx = p.x + dx;
        const cx = nx + 65;
        setTimeout(() => { if (modeRef.current === 'move') { if (cx < 24) setFancyZone('left'); else if (cx > window.innerWidth - 24) setFancyZone('right'); else setFancyZone(null); } }, 0);
        return { x: nx, y: p.y + dy };
      });
    } else if (modeRef.current === 'flick') {
      let nx = e.clientX - startRef.current.x;
      let ny = e.clientY - startRef.current.y;
      const dist = Math.sqrt(nx*nx + ny*ny);
      if (dist > 40) { nx = (nx / dist) * 40; ny = (ny / dist) * 40; }
      offsetRef.current = { x: nx, y: ny };
      if (nubRef.current) nubRef.current.style.transform = `translate(${nx}px, ${ny}px)`;
      
      const absX = Math.abs(nx);
      const absY = Math.abs(ny);
      if (absX > 20 || absY > 20) startHold(absX > absY ? (nx < 0 ? 'Left' : 'Right') : (ny < 0 ? 'Up' : 'Down'));
      else stopHold();
    }
  }, [startHold, stopHold]);

  const onPointerUp = useCallback((e: React.PointerEvent) => {
    e.stopPropagation();
    if (longPressTimerRef.current) {
      clearTimeout(longPressTimerRef.current);
      longPressTimerRef.current = null;
    }

    setFancyZone(null);
    stopHold();
    if (nestledRef.current) return;
    const el = baseRef.current;
    if (!el) return;
    if (el.hasPointerCapture(e.pointerId)) el.releasePointerCapture(e.pointerId);
    
    el.style.background = 'rgba(26, 26, 26, 0.4)';
    el.style.transform = 'scale(1)';

    if (modeRef.current === 'move') {
      el.style.transitionProperty = 'all';
      setPos(p => {
        const cx = p.x + 65;
        let ty = Math.max(24, Math.min(p.y, window.innerHeight - 154));
        if (cx < 24) { setTimeout(() => setNestled('left'), 0); return { x: -65, y: ty }; }
        if (cx > window.innerWidth - 24) { setTimeout(() => setNestled('right'), 0); return { x: window.innerWidth - 65, y: ty }; }
        return { x: Math.max(10, Math.min(p.x, window.innerWidth - 140)), y: ty };
      });
    } else if (modeRef.current === 'flick') {
      el.style.transitionProperty = 'all';
      if (Math.abs(offsetRef.current.x) < 10 && Math.abs(offsetRef.current.y) < 10) {
        if (!longPressedRef.current) {
          dispatchDPad('Enter');
        }
        if (navigator.vibrate) navigator.vibrate(50);
      }
    } else {
      el.style.transitionProperty = 'all';
    }
    
    if (nubRef.current) {
      nubRef.current.style.transition = 'transform 0.2s cubic-bezier(0.2, 0.8, 0.2, 1)';
      nubRef.current.style.transform = `translate(0px, 0px)`;
      setTimeout(() => { if (nubRef.current) nubRef.current.style.transition = 'none'; }, 300);
    }
    modeRef.current = 'idle';
  }, [setNestled, stopHold]);

  const isNestled = nestledState !== null;

  return (
    <>
      <div className={cn("fixed top-0 bottom-0 left-0 w-8 bg-blue-500/20 transition-opacity pointer-events-none z-40 border-r border-blue-500/30", fancyZone === 'left' ? "opacity-100" : "opacity-0")} />
      <div className={cn("fixed top-0 bottom-0 right-0 w-8 bg-blue-500/20 transition-opacity pointer-events-none z-40 border-l border-blue-500/30", fancyZone === 'right' ? "opacity-100" : "opacity-0")} />
      
      {/* Search Bar & Backdrop Stubbed */}
      <div ref={baseRef} className="fixed z-[70] rounded-full flex touch-none transition-all duration-200 ease-out select-none"
        style={{ width: 130, height: 130, left: pos.x, top: pos.y, background: isNestled ? 'transparent' : 'rgba(26, 26, 26, 0.4)', backdropFilter: isNestled ? 'none' : 'blur(16px)', border: isNestled ? '1px solid transparent' : '1px solid rgba(255, 255, 255, 0.1)', boxShadow: isNestled ? 'none' : '0 8px 32px rgba(0, 0, 0, 0.3)', cursor: isNestled ? 'default' : 'grab', pointerEvents: isNestled ? 'none' : 'auto' }}
        onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp} onPointerCancel={onPointerUp}>
        <div className="absolute inset-0 flex items-center justify-center pointer-events-none">
          <div ref={nubRef} className={cn("w-14 h-14 bg-[#121212] rounded-full flex items-center justify-center shadow-inner border border-white/10 pointer-events-auto", isNestled ? "cursor-pointer" : "cursor-grab")}>
            <span className="font-mono text-xs font-bold text-pink-500/90 select-none">&gt;_</span>
          </div>
        </div>
      </div>
    </>
  );
}

export default function App() {
  const [tabs, setTabs] = useState(() => [{ id: Date.now(), title: 'pwsh' }]);
  const [activeId, setActiveId] = useState(tabs[0].id);
  const [showMenu, setShowMenu] = useState(false);
  const [showExitConfirm, setShowExitConfirm] = useState(false);
  const [scripts, setScripts] = useState<string[]>([]);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    createSession(tabs[0].id);
    setScripts(getScripts());
  }, []);

  useEffect(() => {
    const onDpad = (e: CustomEvent) => {
      switch (e.detail) {
        case 'Up':    sendInput(activeId, '\x1b[A'); break;
        case 'Down':  sendInput(activeId, '\x1b[B'); break;
        case 'Right': sendInput(activeId, '\x1b[C'); break;
        case 'Left':  sendInput(activeId, '\x1b[D'); break;
        case 'Enter': sendInput(activeId, '\r');     break;
      }
    };
    window.addEventListener('dpad-input', onDpad as EventListener);

    const onKey = (e: CustomEvent) => sendInput(activeId, e.detail as string);
    window.addEventListener('key-input', onKey as EventListener);

    return () => {
      window.removeEventListener('dpad-input', onDpad as EventListener);
      window.removeEventListener('key-input', onKey as EventListener);
    };
  }, [activeId]);

  const addTab = (cmd?: string, title?: string) => {
    const id = Date.now();
    setTabs(prev => [...prev, { id, title: title || 'pwsh' }]);
    setActiveId(id);
    setShowMenu(false);
    createSession(id);
    
    if (cmd) {
      setTimeout(() => invokeCommand(id, cmd), 100);
    } else {
      setTimeout(() => sendInput(id, '\r'), 100);
    }
  };

  const runProjection = () => {
    startProjection();
    setShowMenu(false);
    const cmd = '$ip = ([System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | % GetIPProperties | % UnicastAddresses | ? { $_.Address.AddressFamily -eq "InterNetwork" -and -not [System.Net.IPAddress]::IsLoopback($_.Address) } | Select -ExpandProperty Address -First 1 | % IPAddressToString); if(!$ip){$ip="your-phone-ip"}; Write-Host "`n[+] Desktop Projection running at http://$ip`:8080`n" -ForegroundColor Cyan';
    setTimeout(() => invokeCommand(activeId, cmd), 100);
  };

  const closeTab = (id: number, e: React.PointerEvent) => {
    e.stopPropagation();
    closeSession(id);
    setTabs(prev => {
      const next = prev.filter(t => t.id !== id);
      if (next.length === 0) {
        minimizeApp();
        const newId = Date.now();
        createSession(newId);
        setTimeout(() => setActiveId(newId), 10);
        return [{ id: newId, title: 'pwsh' }];
      }
      if (activeId === id) setActiveId(next[next.length - 1].id);
      return next;
    });
  };

  useEffect(() => {
    const handleClickOutside = (e: PointerEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setShowMenu(false);
    };
    document.addEventListener('pointerdown', handleClickOutside);
    return () => document.removeEventListener('pointerdown', handleClickOutside);
  }, []);

  return (
    <div className="fixed inset-0 flex flex-col bg-black overflow-hidden font-sans select-none touch-none">
      <div className="shrink-0 bg-[#0C0C0C] border-b border-[#1F1F1F] relative z-[60]" style={{ paddingTop: 'env(safe-area-inset-top, 28px)' }}>
        <div className="flex items-stretch h-10">
          <div className="flex items-center relative" ref={menuRef}>
            <div onPointerDown={() => setShowMenu(!showMenu)} className="w-12 h-full flex items-center justify-center text-gray-400 hover:bg-[#252525] cursor-pointer border-r border-[#1F1F1F]"><Menu size={16} /></div>
            {showMenu && (
              <div className="absolute top-full left-0 mt-1 w-64 bg-[#1A1A1A] border border-[#2A2A2A] rounded-md shadow-2xl z-50 py-2">
                <div className="px-3 pb-1 text-[10px] font-bold text-gray-500 uppercase tracking-wider">System</div>
                <button onPointerDown={runProjection} className="w-full text-left px-4 py-2 text-sm text-gray-200 hover:bg-[#012456] flex items-center gap-3"><MonitorPlay size={14} className="text-emerald-400"/> Cast to Desktop</button>
                
                {scripts.length > 0 && (
                  <>
                    <div className="px-3 pt-3 pb-1 text-[10px] font-bold text-gray-500 uppercase tracking-wider border-t border-[#2A2A2A] mt-1">Scripts</div>
                    {scripts.map(script => (
                      <button key={script} onPointerDown={() => addTab(`./scripts/${script}`, script.replace('.ps1', ''))} className="w-full text-left px-4 py-2 text-sm text-gray-200 hover:bg-[#012456] flex items-center gap-3"><FileCode2 size={14} className="text-purple-400"/> {script.replace('.ps1', '')}</button>
                    ))}
                  </>
                )}
              </div>
            )}
          </div>

          <div className="flex flex-1 overflow-x-auto" style={{ scrollbarWidth: 'none' }}>
            {tabs.map(tab => (
              <div key={tab.id} onPointerDown={() => setActiveId(tab.id)} className={cn("flex items-center gap-2 px-4 min-w-[100px] max-w-[160px] shrink-0 text-xs font-medium cursor-pointer border-t-2", activeId === tab.id ? "bg-[#012456] border-t-[#0078d4] text-white" : "bg-[#1A1A1A] border-t-transparent text-gray-400 hover:bg-[#252525]")}>
                <span className="text-[#0078d4] font-bold">&gt;_</span>
                <span className="flex-1 truncate">{tab.title}</span>
                <span onPointerDown={(e) => closeTab(tab.id, e)} className="opacity-50 hover:opacity-100 p-1"><X size={12} /></span>
              </div>
            ))}
          </div>

          <div className="flex items-center relative">
            <div onPointerDown={() => addTab()} className="w-12 h-full flex items-center justify-center text-gray-400 hover:bg-[#252525] cursor-pointer border-l border-[#1F1F1F]"><Plus size={16} /></div>
            <div onPointerDown={() => minimizeApp()} className="w-12 h-full flex items-center justify-center text-gray-400 hover:bg-[#252525] cursor-pointer border-l border-[#1F1F1F]"><Minus size={16} /></div>
            <div onPointerDown={() => setShowExitConfirm(true)} className="w-12 h-full flex items-center justify-center text-red-500 hover:bg-red-500/20 cursor-pointer border-l border-[#1F1F1F]"><X size={16} /></div>
          </div>
        </div>
      </div>

      <div className="flex-1 relative">
        {tabs.map(tab => (
          <div key={tab.id} className="absolute inset-0" style={{ opacity: tab.id === activeId ? 1 : 0, pointerEvents: tab.id === activeId ? 'auto' : 'none' }}>
            <TerminalEmulator tabId={tab.id} />
          </div>
        ))}
      </div>
      <Powerstick />

      {showExitConfirm && (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/60 backdrop-blur-sm">
           <div className="bg-[#012456] border border-[#1F1F1F] rounded-md overflow-hidden max-w-sm w-full mx-4 shadow-2xl flex flex-col font-mono animate-in fade-in zoom-in-95 duration-150">
              {/* Modal Window Title Bar */}
              <div className="bg-[#0C0C0C] border-b border-[#1F1F1F] px-4 py-2 flex items-center justify-between text-xs text-gray-400 select-none">
                  <div className="flex items-center gap-2 text-[#0078d4] font-bold text-xs uppercase tracking-widest px-2 pb-2 border-b border-[#0078d4]/30">
                    <Terminal size={14} />
                    <span>Terminal - Subsystem Warning</span>
                  </div>
                 <button onPointerDown={() => setShowExitConfirm(false)} className="text-gray-400 hover:text-white p-0.5 transition-colors">
                    <X size={14} />
                 </button>
              </div>

              {/* Modal Dialog Body */}
              <div className="p-6 flex flex-col gap-4">
                 <div className="text-emerald-400 text-sm font-bold select-none">
                    PS /subsystem&gt; Stop-Terminal
                 </div>
                 
                 <div className="text-gray-200 text-sm leading-relaxed">
                    Are you sure you want to exit? All running shell sessions and background processes will be terminated.
                 </div>

                 <div className="flex justify-end gap-3 mt-2">
                    <button 
                       onPointerDown={() => setShowExitConfirm(false)} 
                       className="px-4 py-2 border border-white/20 hover:border-white/40 bg-transparent text-white text-xs font-semibold rounded active:scale-95 transition-all"
                    >
                       Cancel
                    </button>
                    <button 
                       onPointerDown={() => { window.AndroidBridge?.exitApp?.(); setShowExitConfirm(false); }} 
                       className="px-4 py-2 bg-[#C50F1F] hover:bg-red-700 text-white text-xs font-semibold rounded active:scale-95 transition-all shadow-lg shadow-red-950/30"
                    >
                       Exit Subsystem
                    </button>
                 </div>
              </div>
           </div>
        </div>
      )}
    </div>
  );
}

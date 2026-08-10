import React, { useEffect, useRef } from 'react';
import { Terminal as XTerm } from 'xterm';
import { FitAddon } from 'xterm-addon-fit';
import 'xterm/css/xterm.css';
import { subscribeToOutput, sendInput, sendResize, notifyReady } from '../lib/pwshBridge';

export interface TerminalConfig {
  backgroundColor: string;
  foregroundColor: string;
  cursorColor?: string;
  selectionBackground?: string;
  cursorBlink: boolean;
  fontSize: number;
  fontFamily: string;
  fontWeight?: XTerm['options']['fontWeight'];
  cursorStyle?: 'block' | 'underline' | 'bar';
  colors?: Partial<{
    black: string; red: string; green: string; yellow: string;
    blue: string; magenta: string; cyan: string; white: string;
    brightBlack: string; brightRed: string; brightGreen: string; brightYellow: string;
    brightBlue: string; brightMagenta: string; brightCyan: string; brightWhite: string;
  }>;
}

const DEFAULT_CONFIG: TerminalConfig = {
  backgroundColor: '#012456',
  foregroundColor: '#CCCCCC',
  cursorColor:     '#FFFFFF',
  cursorBlink:     true,
  fontSize:        14,
  fontFamily:      '"Cascadia Code", "Cascadia Mono", Consolas, monospace',
  cursorStyle:     'bar',
  colors: {
    black:         '#0C0C0C',
    red:           '#C50F1F',
    green:         '#13A10E',
    yellow:        '#C19C00',
    blue:          '#0037DA',
    magenta:       '#881798',
    cyan:          '#3A96DD',
    white:         '#CCCCCC',
    brightBlack:   '#767676',
    brightRed:     '#E74856',
    brightGreen:   '#16C60C',
    brightYellow:  '#F9F1A5',
    brightBlue:    '#3B78FF',
    brightMagenta: '#B4009E',
    brightCyan:    '#61D6D6',
    brightWhite:   '#F2F2F2',
  },
};

interface Props { config?: Partial<TerminalConfig>; tabId: number; }

export function TerminalEmulator({ config: configProp, tabId }: Props) {
  const config = { ...DEFAULT_CONFIG, ...configProp };
  const containerRef = useRef<HTMLDivElement>(null);
  const xtermRef     = useRef<XTerm | null>(null);
  const fitRef       = useRef<FitAddon | null>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const term = new XTerm({
      cursorBlink:  config.cursorBlink,
      cursorStyle:  config.cursorStyle ?? 'bar',
      fontSize:     config.fontSize,
      fontFamily:   config.fontFamily,
      fontWeight:   config.fontWeight,
      scrollback:   5000,
      allowTransparency: true,
      theme: {
        background:          config.backgroundColor,
        foreground:          config.foregroundColor,
        cursor:              config.cursorColor,
        selectionBackground: config.selectionBackground,
        ...config.colors,
      },
    });

    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(containerRef.current);
    term.focus();

    xtermRef.current = term;
    fitRef.current   = fit;

    setTimeout(() => {
      try { fit.fit(); } catch {}
      notifyReady();

      const xtermEl = containerRef.current?.querySelector('.xterm') as HTMLElement | null;
      const viewport = xtermEl?.querySelector('.xterm-viewport') as HTMLElement | null;
      const screen = xtermEl?.querySelector('.xterm-screen') as HTMLElement | null;

      if (viewport && screen) {
        viewport.style.touchAction = 'pan-y';
        (viewport.style as CSSStyleDeclaration & { webkitOverflowScrolling?: string })
          .webkitOverflowScrolling = 'touch';

        viewport.addEventListener('scroll', () => {
          const cellHeight = (term as any)._core?._renderService?.dimensions?.actualCellHeight ?? 18;
          const offset = viewport.scrollTop % cellHeight;
          screen.style.transform = `translateY(-${offset}px)`;
        }, { passive: true });
      }
    }, 50);

    term.onData(data => sendInput(tabId, data));
    term.onResize(({ cols, rows }) => sendResize(tabId, cols, rows));

    const unsub = subscribeToOutput(tabId, text => {
      xtermRef.current?.write(text);
    });

    const onResize = () => {
      requestAnimationFrame(() => {
        if (!fitRef.current || !containerRef.current?.offsetParent) return;
        try { fitRef.current.fit(); } catch {}
      });
    };
    window.addEventListener('resize', onResize);

    const TAP_SLOP = 8;
    let tsX = 0, tsY = 0, didMove = false;
    const onTouchStart = (e: TouchEvent) => {
      tsX = e.touches[0].clientX;
      tsY = e.touches[0].clientY;
      didMove = false;
    };
    const onTouchMove = (e: TouchEvent) => {
      const dx = Math.abs(e.touches[0].clientX - tsX);
      const dy = Math.abs(e.touches[0].clientY - tsY);
      if (dx > TAP_SLOP || dy > TAP_SLOP) didMove = true;
    };
    const onTouchEnd = () => { if (!didMove && !document.body.classList.contains('hud-menu-active')) term.focus(); };

    const onFocusClick = () => { if (!document.body.classList.contains('hud-menu-active')) term.focus(); };
    containerRef.current?.addEventListener('click', onFocusClick);

    const el = containerRef.current;
    el?.addEventListener('touchstart', onTouchStart, { passive: true });
    el?.addEventListener('touchmove',  onTouchMove,  { passive: true });
    el?.addEventListener('touchend',   onTouchEnd,   { passive: true });

    return () => {
      window.removeEventListener('resize', onResize);
      el?.removeEventListener('touchstart', onTouchStart);
      el?.removeEventListener('touchmove',  onTouchMove);
      el?.removeEventListener('touchend',   onTouchEnd);
      
      unsub();
      term.dispose();
      xtermRef.current = null;
      fitRef.current   = null;
    };
  }, []);

  useEffect(() => {
    const term = xtermRef.current;
    if (!term) return;
    term.options.cursorBlink  = config.cursorBlink;
    term.options.cursorStyle  = config.cursorStyle ?? 'bar';
    term.options.fontSize     = config.fontSize;
    term.options.fontFamily   = config.fontFamily;
    term.options.fontWeight   = config.fontWeight;
    term.options.theme = {
      background:          config.backgroundColor,
      foreground:          config.foregroundColor,
      cursor:              config.cursorColor,
      selectionBackground: config.selectionBackground,
      ...config.colors,
    };
    requestAnimationFrame(() => {
      if (!fitRef.current || !containerRef.current?.offsetParent) return;
      try { fitRef.current.fit(); } catch {}
    });
  }, [config.backgroundColor, config.foregroundColor, config.cursorBlink,
      config.fontSize, config.fontFamily, config.cursorStyle]);

  return (
    <div
      className="absolute inset-0"
      style={{
        backgroundColor: config.backgroundColor,
        paddingTop:    '4px',
        paddingBottom: 'max(8px, env(safe-area-inset-bottom))',
        paddingLeft:   '4px',
        paddingRight:  '4px',
      }}
    >
      <style>{`
        .xterm-viewport::-webkit-scrollbar { width: 6px; }
        .xterm-viewport::-webkit-scrollbar-track { background: transparent; }
        .xterm-viewport::-webkit-scrollbar-thumb { background: rgba(255,255,255,0.15); border-radius: 3px; }
      `}</style>
      <div ref={containerRef} className="w-full h-full overflow-hidden" />
    </div>
  );
}

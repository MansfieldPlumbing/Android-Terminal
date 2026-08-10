import express from "express";
import path from "path";
import fs from "fs/promises";
import { createServer as createViteServer } from "vite";

async function startServer() {
  const app = express();
  const PORT = 3000;

  app.use(express.json());

  // API routes FIRST
  app.get("/api/files", async (req, res) => {
    try {
      const configIni = await fs.readFile(path.join(process.cwd(), 'config.ini'), 'utf8').catch(() => null);
      
      const files: Record<string, string> = {
        '/scripts/txt.ps1': `function Get-NetworkStats {
    [CmdletBinding()]
    param (
        [string]$InterfaceAlias = '*'
    )

    Get-NetAdapterStatistics -Name $InterfaceAlias | 
    Select-Object Name, ReceivedBytes, SentBytes, ReceivedDiscardedPackets
}

# Monitor loop
while ($true) {
    Clear-Host
    Get-NetworkStats | Format-Table -AutoSize
    Start-Sleep -Seconds 2
}`
      };

      if (configIni) {
        files['/config.ini'] = configIni;
      } else {
        files['/config.ini'] = `[Appearance]
ApplicationTheme=Dark
AlwaysShowTabs=true
TabWidthMode=Equal
PaneAnimations=true

[Profile.Default]
Name=Windows PowerShell
Commandline=powershell.exe
ColorScheme=Campbell
FontFace=Cascadia Mono
FontSize=12
FontWeight=Normal
CursorShape=Bar
CursorColor=#FFFFFF
CursorBlink=true
Padding=8, 8, 8, 8
ScrollbarVisibility=Visible
BackgroundOpacity=100
EnableAcrylic=false

[ColorSchemes.Campbell]
Foreground=#CCCCCC
Background=#0C0C0C
CursorColor=#FFFFFF
SelectionBackground=#FFFFFF
Black=#0C0C0C
Red=#C50F1F
Green=#13A10E
Yellow=#C19C00
Blue=#0037DA
Purple=#881798
Cyan=#3A96DD
White=#CCCCCC
BrightBlack=#767676
BrightRed=#E74856
BrightGreen=#16C60C
BrightYellow=#F9F1A5
BrightBlue=#3B78FF
BrightPurple=#B4009E
BrightCyan=#61D6D6
BrightWhite=#F2F2F2

[Keybindings]
Copy=ctrl+c
Paste=ctrl+v
Find=ctrl+shift+f
NewTab=ctrl+shift+t
CloseTab=ctrl+shift+w

[Controls]
DPadArrows=true
`;
      }

      res.json(files);
    } catch (e) {
      res.status(500).json({ error: String(e) });
    }
  });

  app.post("/api/files", async (req, res) => {
    try {
      if (req.body.path === '/config.ini') {
        await fs.writeFile(path.join(process.cwd(), 'config.ini'), req.body.content, 'utf8');
      }
      res.json({ success: true });
    } catch (e) {
      res.status(500).json({ error: String(e) });
    }
  });

  // Vite middleware for development
  if (process.env.NODE_ENV !== "production") {
    const vite = await createViteServer({
      server: { middlewareMode: true },
      appType: "spa",
    });
    app.use(vite.middlewares);
  } else {
    const distPath = path.join(process.cwd(), 'dist');
    app.use(express.static(distPath));
    app.get('*all', (req, res) => {
      res.sendFile(path.join(distPath, 'index.html'));
    });
  }

  app.listen(PORT, "0.0.0.0", () => {
    console.log(`Server running on http://localhost:${PORT}`);
  });
}

startServer();

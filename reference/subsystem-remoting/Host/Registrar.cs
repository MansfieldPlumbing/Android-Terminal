using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Android.Content.Res;

namespace Subsystem;

// Registrar — the filesystem -> registry bridge (REGISTRY-SPEC §1 "Bootstrapping"). Discovers the
// built-in presenter/system surfaces in the APK's shell and registers each as a `Cm` capability with a
// manifest, so that /apps and /shell-layout become `Cm` queries (resolve-by-id) instead of raw
// filesystem scans. The filesystem stops being the source of truth — it becomes a seed for the registry.
//
// Idempotent (Cm.Register upserts on Path) and reconciling (drops Presenter/System records whose source
// no longer exists, so deleting a presenter .obp removes it from the registry too).
// Guarded: never throws (the project "never crash, degrade and record" rule).
public static class Registrar
{
    // Default NF glyph icons the bootstrap assigns by id. A manifest-declared icon overrides this later;
    // for now the registrar seeds a sensible default. Values are literal Nerd-Font PUA glyphs — keep
    // this file UTF-8 and don't let tooling that assumes ASCII rewrite it.
    private static readonly IReadOnlyDictionary<string, string> IconById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["agent"]       = "󰚩",  // agent
        ["terminal"]    = "",  // terminal
        ["files"]       = "",  // folder
        ["taskmgr"]     = "󰄪",  // bar-chart
        ["edit"]        = "󰏫",  // pencil
        ["graph"]       = "󰄪",  // share/graph
        ["screen"]      = "󰍹",  // desktop/screen
        ["minesweeper"] = "󰀼",  // bomb/game
        ["settings"]    = "",  // gear
        ["morse"]       = "󰤨",  // signal/radio
        ["dptest"]      = "",  // directport interop
    };

    // First-class (pinned) surfaces. console removed — agent is the chat we grow.
    private static readonly HashSet<string> FirstClass =
        new(StringComparer.OrdinalIgnoreCase) { "terminal", "agent", "files" };

    // Launcher group per presenter id (the registry's truth — presenters live FLAT on disk; a
    // folder name is never a group). Unlisted ids default to "tools" so a drop-in just appears.
    private static readonly IReadOnlyDictionary<string, string> GroupById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["terminal"] = "core",
        ["agent"]    = "core",
        ["files"]    = "tools",
        ["edit"]     = "tools",
        ["taskmgr"]  = "applets",
        ["morse"]    = "tools",
    };

    // Pulled from the launcher: graph/screen are half-real (owner directive 2026-06-11); webnn
    // belongs to Settings. Skip-seeded, not deleted — the .obp files stay, but an un-seeded id
    // reconciles away and is invisible until deliberately re-listed.
    private static readonly HashSet<string> SkipIds =
        new(StringComparer.OrdinalIgnoreCase) { "graph", "screen", "webnn", "morse" };

    // System-tier presenters (\Capability\System\*, group "system").
    private static readonly HashSet<string> SystemIds =
        new(StringComparer.OrdinalIgnoreCase) { "settings" };

    public static void SeedFromAssets(AssetManager? assets)
    {
        if (assets == null) return;
        try
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            static string? PresenterExt(string f) =>
                f.EndsWith(".obp") ? ".obp" : f.EndsWith(".html") ? ".html" : null;

            // 1. Presenters: shell/presenters/*.obp — a FLAT directory, enumerated from the COMPILED
            // catalog (ObpHost). NT discipline: the file tree holds NO truth — group, tier, icon and
            // first-class come from the seed tables here (and .ssr data can override the records),
            // never from folder names. Dropping a new .obp in = it appears, grouped by the registry.
            foreach (var vpath in ObpHost.Enumerate("shell/presenters"))
            {
                var parts = vpath.Split('/');                  // shell / presenters / <file>
                if (parts.Length != 3) continue;
                var f = parts[2];
                var ext = PresenterExt(f);
                if (ext == null) continue;
                var id = f.Substring(0, f.Length - ext.Length).ToLowerInvariant();
                if (id.Length == 0 || SkipIds.Contains(id)) continue;
                var name = char.ToUpper(id[0]) + id.Substring(1);
                // A user surface is a PRESENTER (it presents a namespace subtree — the .obp object,
                // REGISTRY-SPEC §9). The SYSTEM tier (settings) is parked out as its own NT category,
                // the .cpl-vs-.exe split — never folded into Presenter.
                bool system = SystemIds.Contains(id);
                var path = (system ? "\\Capability\\System\\" : "\\Capability\\Presenter\\") + id;
                var manifest = new
                {
                    version = 1,
                    id,
                    name,
                    kind = system ? "system" : "presenter",
                    group = system ? "system" : (GroupById.TryGetValue(id, out var grp) ? grp : "tools"),
                    file = "presenters/" + f,
                    icon = IconById.TryGetValue(id, out var g) ? g : "",
                    firstClass = !system && FirstClass.Contains(id),
                    consumers = new[] { "canvas" }
                };
                Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                {
                    Path = path,
                    Name = name,
                    Type = system ? "System" : "Presenter",
                    Source = "obp:" + vpath,
                    ManifestJson = JsonSerializer.Serialize(manifest),
                    Integrity = "User",
                    StartType = "manual",
                    Enabled = true,
                });
                present.Add(path);
            }

            // 2b. The BACKGROUND (bg) — the spatial PowerShell desktop (src/shell/bg/, outside presenters/).
            // role:"desktop" — the Shell's RESTING LAYER (mounted under every window), not a launchable
            // applet: launcher presenters (Menu/TaskView) skip desktop-role records; the Shell resolves
            // it by role.
            {
                // The file field reflects whichever presenter actually shipped (.obp post-rename;
                // ObpHost's extension alias covers the transition either way).
                var sf = ObpHost.Enumerate("shell/bg")
                             .Select(p => p.Split('/').Last())
                             .FirstOrDefault(f => PresenterExt(f) != null) ?? "bg.obp";
                var sp = "\\Capability\\Presenter\\bg";
                var sm = new
                {
                    version = 1, id = "bg", name = "Background", kind = "presenter", group = "core",
                    file = "bg/" + sf, icon = "", role = "desktop", consumers = new[] { "canvas" }
                };
                Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                {
                    Path = sp, Name = "Background", Type = "Presenter", Source = "obp:shell/bg/" + sf,
                    ManifestJson = JsonSerializer.Serialize(sm), Integrity = "User", StartType = "manual", Enabled = true,
                });
                present.Add(sp);
            }

            // 1c. HTML APPLETS — content/html-applets/*.html, shipped LOOSE in the APK as AndroidAssets
            // (assets/shell/html-applets/; ObpHost's asset-fallback rung serves them — deliberately
            // loose and view-source-able, the single-file-app genre). Same registry discipline as
            // presenters: drop a .html in and it appears in /apps; delete it and the reconcile drops
            // its record. Public vocabulary is "html-applet"; the Cm Type stays Presenter
            // (kind:"applet") so the legacy Type="Applet" purge below never matches these.
            foreach (var vpath in ObpHost.Enumerate("shell/html-applets"))
            {
                var f = vpath.Split('/').Last();
                if (!f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
                var id = f[..^5].ToLowerInvariant();
                if (id.Length == 0) continue;
                var name = char.ToUpper(id[0]) + id.Substring(1);
                var path = "\\Capability\\Presenter\\" + id;
                var manifest = new
                {
                    version = 1,
                    id,
                    name,
                    kind = "applet",
                    group = "applets",
                    file = "html-applets/" + f,
                    icon = IconById.TryGetValue(id, out var g) ? g : "",
                    firstClass = false,
                    consumers = new[] { "canvas" }
                };
                Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                {
                    Path = path,
                    Name = name,
                    Type = "Presenter",
                    Source = "asset:" + vpath,
                    ManifestJson = JsonSerializer.Serialize(manifest),
                    Integrity = "User",
                    StartType = "manual",
                    Enabled = true,
                });
                present.Add(path);
            }

            // 3. The shell layout — chrome objects the Shell mounts, in order.
            void Layout(string id, object manifest)
            {
                var lp = "\\Shell\\Layout\\" + id;
                Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                {
                    Path = lp,
                    Name = id,
                    Type = "ShellObject",
                    ManifestJson = JsonSerializer.Serialize(manifest),
                    Integrity = "System",
                    StartType = "auto",
                    Enabled = true,
                });
                present.Add(lp);
            }
            Layout("menu", new { version = 1, id = "menu", type = "menu", path = "\\Shell\\Menu", order = 0 });
            Layout("taskbar", new { version = 1, id = "taskbar", type = "taskbar", path = "\\Shell\\Taskbar", position = "bottom", order = 1 });
            // The charm bar — the LEFT-edge swipe-in presenter (we own the side edges; MainActivity
            // excludes them from the OS back gesture). Same mechanism as the Menu: a viewport onto
            // the active object's verbs + the system objects — never its own list (REGISTRY-SPEC §9).
            Layout("charms", new { version = 1, id = "charms", type = "charms", path = "\\Shell\\Charms", position = "left", order = 2 });
            // TaskView — the full-screen Start-with-tasks (Windows-8 ergonomics). With Charms it can
            // make the taskbar a desktop-view personality; which chrome mounts is THIS data, not code.
            Layout("taskview", new { version = 1, id = "taskview", type = "taskview", path = "\\Shell\\TaskView", order = 3 });

            // 3b-2. THE FRONT DOOR (REGISTRY-SPEC §9: front door = presenter × token). Which presenter
            // answers "/" — swap `file` to mount a different shell over the SAME namespace (cylon,
            // GERTY, kiosk…), then Invoke-ShellReload. Seeded once, NEVER reconciled-overwritten: a
            // user/agent door choice survives reboots (Cm.Register upserts, so seed only if absent).
            if (Subsystem.Cm.Cm.Get("\\Shell\\FrontDoor") == null)
            {
                Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                {
                    Path = "\\Shell\\FrontDoor", Name = "FrontDoor", Type = "FrontDoor",
                    Integrity = "Admin", StartType = "auto", Enabled = true,
                    ManifestJson = JsonSerializer.Serialize(new
                    {
                        version = 1, id = "default", file = "shell.obp",
                        note = "Swap `file` to any compiled .obp to mount a different front door.",
                    }),
                });
            }

            // (3b retired 2026-06-11: the .ssr file-format import is GONE — owner call, "a bad take we
            // never truly needed." Verbs remain first-class Cm records; they register at runtime via
            // Register-Capability or presenter menu-context, never via a .reg-style file. The reconcile
            // below drops any obp:-sourced Verb records a previous build imported.)

            // 3c. Themes — register each as a Cm capability (kind:theme) so a theme is an OBJECT in the
            // namespace and the gallery is a Cm QUERY, not UI-owned truth (REGISTRY-SPEC §4). themes.json is
            // the seed source; /themes serves the Cm copy.
            try
            {
                var themesJson = ObpHost.ReadAllText("shell/themes.json")
                    ?? throw new System.IO.FileNotFoundException("themes.json");
                using var tdoc = JsonDocument.Parse(themesJson);
                foreach (var prop in tdoc.RootElement.EnumerateObject())
                {
                    var tp = "\\Capability\\Theme\\" + prop.Name;
                    Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                    {
                        Path = tp,
                        Name = prop.Name,
                        Type = "Theme",
                        Source = "asset:shell/themes.json",
                        ManifestJson = prop.Value.GetRawText(),
                        Integrity = "User",
                        StartType = "manual",
                        Enabled = true,
                    });
                    present.Add(tp);
                }
            }
            catch (Exception ex) { Dg.Warn("registrar", ex); }

            // 3c-2. Gr's shader catalog — fragment-shader programs as PLAYLIST objects
            // (\Capability\Shader\<playlist>). "Shader" is the mechanism name (Cutler law); the
            // word "wallpaper" survives only at the Android leaf (the Wp port + the
            // \Shell\SystemWallpaper policy record below) where it is the host OS's own vocabulary.
            // The unit of selection is a playlist that cycles its members, not one record per .frag.
            // Which playlist a shader belongs to is DATA (manifest.json `playlist` + display `name`);
            // the legacy clouds-*/bliss-xp prefix rule remains the fallback for unlisted files.
            // manifest.json still contributes per-frame prompts; `file` (first member) stays for
            // back-compat single-file consumers. Stale records a previous build seeded (incl. the
            // retired \Capability\Wallpaper\* type) reconcile away on next boot.
            try
            {
                var wallpaperNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var playlistOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var playlistTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var manifestText = ObpHost.ReadAllText("shell/shaders/manifest.json");
                if (manifestText != null)
                {
                    using var wdoc = JsonDocument.Parse(manifestText);
                    foreach (var entry in wdoc.RootElement.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("file", out var fv) || fv.ValueKind != JsonValueKind.String) continue;
                        var file = fv.GetString()!;
                        if (entry.TryGetProperty("prompt", out var pv) && pv.ValueKind == JsonValueKind.String)
                            wallpaperNames[file] = pv.GetString()!.Trim();
                        if (entry.TryGetProperty("playlist", out var plv) && plv.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(plv.GetString()))
                        {
                            var pl = plv.GetString()!.Trim();
                            playlistOf[file] = pl;
                            if (entry.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(nv.GetString()) && !playlistTitle.ContainsKey(pl))
                                playlistTitle[pl] = nv.GetString()!.Trim();
                        }
                    }
                }

                var frags = ObpHost.Enumerate("shell/shaders")
                    .Where(p => p.EndsWith(".frag"))
                    .Select(p => p.Substring(p.LastIndexOf('/') + 1))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                void Playlist(string id, string name, List<string> members)
                {
                    if (members.Count == 0) return;   // absent set = no record; reconcile keeps it gone
                    var sp = "\\Capability\\Shader\\" + id;
                    Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                    {
                        Path = sp,
                        Name = name,
                        Type = "Shader",
                        Source = "obp:shell/shaders",
                        ManifestJson = JsonSerializer.Serialize(new
                        {
                            version = 1, id, name, kind = "shader",
                            file = "shaders/" + members[0],
                            files = members.Select(f => new
                            {
                                file = "shaders/" + f,
                                prompt = wallpaperNames.TryGetValue(f, out var p) ? p : "",
                            }).ToArray(),
                            cycleSeconds = 90,
                            language = "glsl-es100",
                        }),
                        Integrity = "User",
                        StartType = "manual",
                        Enabled = true,
                    });
                    present.Add(sp);
                }

                // Group by the manifest's playlist field; a file the manifest doesn't claim keeps
                // the legacy prefix rule (clouds-* → clouds, everything else → bliss-xp).
                string GroupOf(string f) => playlistOf.TryGetValue(f, out var pl) ? pl
                    : (f.StartsWith("clouds-", StringComparison.OrdinalIgnoreCase) ? "clouds" : "bliss-xp");
                string TitleOf(string id) => playlistTitle.TryGetValue(id, out var n) ? n
                    : id.Equals("clouds", StringComparison.OrdinalIgnoreCase) ? "Clouds"
                    : id.Equals("bliss-xp", StringComparison.OrdinalIgnoreCase) ? "Bliss XP"
                    : char.ToUpperInvariant(id[0]) + id.Substring(1);

                foreach (var group in frags.GroupBy(GroupOf, StringComparer.OrdinalIgnoreCase)
                                           .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    Playlist(group.Key.ToLowerInvariant(), TitleOf(group.Key), group.ToList());

                // The SYSTEM wallpaper selection (\Shell\SystemWallpaper) — which Shader playlist
                // the native Wp engine renders on the launcher ("wallpaper" = Android's word, kept
                // at the Android leaf). Seed-if-absent: a user/agent choice (Set-SystemWallpaper)
                // survives reboots, the seed only covers first boot.
                if (Subsystem.Cm.Cm.Get("\\Shell\\SystemWallpaper") == null)
                {
                    Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                    {
                        Path = "\\Shell\\SystemWallpaper", Name = "SystemWallpaper", Type = "ShellObject",
                        Integrity = "User", StartType = "auto", Enabled = true,
                        ManifestJson = JsonSerializer.Serialize(new { version = 1, id = "system-wallpaper", playlist = "bliss-xp" }),
                    });
                }
            }
            catch (Exception ex) { Subsystem.Dg.Warn("registrar", ex); }

            // 3d. Remoting transports — each is a \Capability\ mount (REMOTING.md §3: a rocker toggle whose
            // manifest is simultaneously the switch, the permission, and the agent tool). The host consults
            // .Enabled before serving the endpoint, so the capability record is the ONE truth for whether the
            // surface is reachable (authority is possession, not a hardcoded route). Object-remoting rides the
            // existing loopback host (BindGuard keeps it loopback-only; reach it via `adb forward tcp:8080`).
            void Transport(string id, string name, object manifest, bool enabled, string integrity)
            {
                var tp = "\\Capability\\Remoting\\" + id;
                Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                {
                    Path = tp, Name = name, Type = "Mount", Owner = "\\System", Integrity = integrity,
                    StartType = "manual", Enabled = enabled, ManifestJson = JsonSerializer.Serialize(manifest),
                });
            }
            Transport("Clixml", "Object Remoting (CLIXML)", new
            {
                version = 1, id = "clixml", name = "Object Remoting (CLIXML)", kind = "transport",
                endpoint = "/clixml", method = "POST", payload = "clixml", scheme = "loopback-http",
                description = "PSRP-flavored object remoting: POST a PowerShell command, receive CLIXML " +
                              "(full type + stream fidelity) that deserializes back into live PSObjects.",
            }, enabled: true, integrity: "Admin");
            Transport("Psrp", "PowerShell Remoting (PSRP)", new
            {
                version = 1, id = "psrp", name = "PowerShell Remoting (PSRP)", kind = "transport",
                endpoint = "/psrp", method = "POST", payload = "json", scheme = "named-pipe+loopback-http",
                pipeName = Subsystem.Rs.PipeName,
                routes = new[] { "/psrp/session", "/psrp/invoke", "/psrp/close" },
                idleMinutes = 10,
                description = "Real MS-PSRP remoting: a named-pipe endpoint (Unix domain socket in the " +
                              "app's private cache dir — the same server path as pwsh -CustomPipeName) " +
                              "plus the loopback HTTP seam that brokers per-presenter remote runspaces over " +
                              "it. Structured commands only: parameters cross as data, never spliced " +
                              "script text. One live pipe session at a time (SMA public-API bound; " +
                              "parked: per-session listeners); sessions are VOM owners under \\Sessions\\Psrp.",
            }, enabled: true, integrity: "Admin");

            // 3e. Agent objects — tools, models, and the quick-assist config, all as Cm capabilities so the
            // agent surface is registry-projected (doctrine: everything is an object in one namespace).
            SeedAgentObjects(assets);

            // 4. Reconcile: drop seeded-from-content records whose source is gone (deletes propagate
            // to the registry — removing a presenter/theme/shader file removes its object).
            foreach (var rec in Subsystem.Cm.Cm.List())
            {
                // "Applet" is the LEGACY type — kept here only so a device upgraded from a pre-rename
                // build drops its orphaned \Capability\Applet\* records on first boot (no Presenter
                // record names those paths anymore, so they reconcile away cleanly).
                // "Wallpaper" joins "Applet" as a LEGACY type: a device upgraded from a pre-rename
                // build drops its orphaned \Capability\Wallpaper\* records here (Shader replaced them).
                if ((rec.Type == "Presenter" || rec.Type == "System" || rec.Type == "Theme"
                     || rec.Type == "Shader" || rec.Type == "Wallpaper" || rec.Type == "Applet")
                    && !present.Contains(rec.Path))
                {
                    Subsystem.Cm.Cm.Unregister(rec.Path);
                }
                // Verb records that a pre-retirement build imported from shipped .ssr files (Source
                // "obp:...") are orphans now that the .ssr lane is gone; runtime-registered verbs
                // (Register-Capability / menu-context) carry no obp: source and are untouched.
                else if (rec.Type == "Verb" && (rec.Source?.StartsWith("obp:") ?? false))
                {
                    Subsystem.Cm.Cm.Unregister(rec.Path);
                }
            }

            Subsystem.Dg.Log("registrar", "seeded " + present.Count + " capabilities from assets");
        }
        catch (Exception ex)
        {
            Subsystem.Dg.Log("registrar", "seed failed: " + ex.Message);
        }
    }

    // Seeds the agent's registry-projected objects. Idempotent (Cm upserts on Path). These are the
    // bootstrap; the agent reads them live, so editing/adding a record changes her abilities with no
    // recompile — the registry IS the agent's configuration.
    private static void SeedAgentObjects(Android.Content.Res.AssetManager assets)
    {
        void Reg(string path, string name, string type, object manifest, bool enabled = true, string integrity = "User") =>
            Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
            {
                Path = path, Name = name, Type = type, Owner = "\\Agent", Integrity = integrity,
                StartType = "manual", Enabled = enabled, ManifestJson = JsonSerializer.Serialize(manifest),
            });

        // --- Registry-projected agent tools. Each is a Cm capability whose manifest carries an `agentTool`
        //     block (AgentTools.RegistryTools surfaces it as a LiteRT-LM tool; `command` runs in the runspace
        //     with the model's args exposed as $ToolArgs). The definitions live in DATA — shell/agent-tools.json
        //     (authored at src/shell/agent-tools.json) — NOT in C#: a tool is added by editing the data file (no
        //     recompile), and the model-facing snake_case names never enter the source. Mirrors the themes.json
        //     seed: the registry is the one truth, the .json is just an import source. ---
        try
        {
            var toolsJson = ObpHost.ReadAllText("shell/agent-tools.json")
                ?? throw new System.IO.FileNotFoundException("agent-tools.json");
            using var atdoc = JsonDocument.Parse(toolsJson);
            foreach (var t in atdoc.RootElement.EnumerateArray())
            {
                var tname = t.TryGetProperty("name", out var nv) ? (nv.GetString() ?? "") : "";
                var cmd   = t.TryGetProperty("command", out var cv) ? (cv.GetString() ?? "") : "";
                if (tname.Length == 0 || cmd.Length == 0) continue;
                object pars = t.TryGetProperty("parameters", out var pv)
                    ? (object)pv : new { type = "object", properties = new { } };
                var manifest = new
                {
                    version = 1,
                    kind = "agentTool",
                    agentTool = new
                    {
                        name = tname,
                        description = t.TryGetProperty("description", out var dv) ? (dv.GetString() ?? "") : "",
                        parameters = pars,
                        command = cmd
                    }
                };
                Reg("\\Capability\\AgentTool\\" + tname, tname, "AgentTool", manifest);
            }
        }
        catch (Exception ex) { Subsystem.Dg.Log("registrar", "agent-tools.json seed failed: " + ex.Message); }

        // --- Registry-projected system prompts (\Capability\Prompt\<id>) — the Prompt half of the agent's
        //     Prompt/Response contract. The text lives in DATA (shell/prompts.json, a dict keyed by id); the
        //     RuntimeBroker resolves a record live and hands it to the runtime. No head holds the prompt as
        //     its own truth — edit the data file and the agent's voice changes with no recompile (same
        //     discipline as agent-tools.json). ---
        try
        {
            var promptsJson = ObpHost.ReadAllText("shell/prompts.json")
                ?? throw new System.IO.FileNotFoundException("prompts.json");
            using var pdoc = JsonDocument.Parse(promptsJson);
            foreach (var entry in pdoc.RootElement.EnumerateObject())
            {
                if (entry.Name.StartsWith("_")) continue;                  // _about and other meta keys
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var e = entry.Value;
                var pid = entry.Name;
                var pname = e.TryGetProperty("name", out var pnv) ? (pnv.GetString() ?? pid) : pid;
                Reg("\\Capability\\Prompt\\" + pid, pname, "Prompt", new
                {
                    version = 1,
                    kind = "prompt",
                    id = pid,
                    role = e.TryGetProperty("role", out var prv) ? (prv.GetString() ?? "") : "",
                    systemInstruction = e.TryGetProperty("systemInstruction", out var psv) ? (psv.GetString() ?? "") : "",
                });
            }
        }
        catch (Exception ex) { Subsystem.Dg.Log("registrar", "prompts.json seed failed: " + ex.Message); }

        // --- Consent capabilities (\Capability\Consent\*) — the owner's opt-in permission set: the ONE
        //     truth the Settings → Permissions presenter projects, and the same gate the agent's hardware
        //     tools check (AgentTools.HardwareConsentGranted reads \Capability\Consent\Hardware). Every
        //     consent is OFF by default (OPT-IN), carries its blast radius (INFORMED), and is revocable
        //     (DESTROYABLE: Set-Capability -Enabled:$false kills the possession gate immediately; the
        //     presenter also calls the bridge's revokePermission for the OS layer). SEED-IF-ABSENT so a
        //     granted permission is never re-disabled on reboot. consentKind: capability | android |
        //     allfiles | accessibility (tells the presenter which grant/revoke mechanism to drive). ---
        void Consent(string id, string name, string description, string consentKind, string? androidPerm = null, bool devDefault = false)
        {
            var path = "\\Capability\\Consent\\" + id;
            if (Subsystem.Cm.Cm.Get(path) is not null) return;     // never clobber a live grant
            bool enabled = false;
#if DEV
            // Scott-directed: "only a --debug build should opt in for me." A DEV build (DefineConstants;DEV,
            // csproj — compiled OUT on -p:SubsystemRelease=true) self-grants the marked dev-workflow consents
            // so the owner is not hand-granting on every install. Release stays default-deny/opt-in. Only
            // consents that are pure Cm gates (no OS-side prompt) AND not network-exposing are marked
            // devDefault; the sensitive/OS-backed ones stay OFF even on DEV.
            enabled = devDefault;
#endif
            Reg(path, name, "Consent", new { version = 1, kind = "consent", description, consentKind, androidPerm },
                enabled: enabled, integrity: "User");
        }
        try
        {
            Consent("Hardware", "Device hardware",
                "Let the assistant operate device hardware — torch and vibration. While off, hardware tool-calls from the model are refused.",
                "capability");
            Consent("Microphone", "Microphone",
                "Microphone capture for voice interaction and audio scripts.",
                "android", "android.permission.RECORD_AUDIO");
            Consent("Camera", "Camera",
                "Camera capture for vision scripts and remote camera.",
                "android", "android.permission.CAMERA");
            Consent("Location", "Location",
                "Precise GPS location for scripts and automation.",
                "android", "android.permission.ACCESS_FINE_LOCATION");
            Consent("Storage", "Storage (All files)",
                "Read and write the entire Android filesystem from PowerShell (All-files access).",
                "allfiles");
            Consent("ScreenCapture", "Screen capture",
                "Capture and broadcast the screen (MediaProjection). Android still confirms at capture time.",
                "capability");
            Consent("External", "External connections",
                "Bind the control server to the local network (0.0.0.0) instead of loopback only — exposes the device to other machines on your Wi-Fi.",
                "capability");
            Consent("Accessibility", "Accessibility service",
                "Read on-screen content and synthesize input through the Accessibility service.",
                "accessibility");
            Consent("AdbElevation", "ADB self-elevation",
                "Let the app connect to the on-device ADB channel and run as the uid=2000 shell (the dev/remote-control elevation). DEV builds only — a release build cannot do this at all. Off = no elevation, no mDNS multicast.",
                "capability", devDefault: true);
        }
        catch (Exception ex) { Subsystem.Dg.Log("registrar", "consent seed failed: " + ex.Message); }

        // Build-posture policy (\System\Policies\* — the NT HKLM\Software\Policies analog). The auditable
        // projection of the COMPILE-TIME ceiling: a release build hard-blocks AdbElevation regardless of the
        // consent record (the self-elevation path is not in the binary). System integrity; the runspace/agent
        // read it, the ceiling itself is the compiler. Upserted every boot to reflect the actual binary.
        try
        {
            string posture =
#if DEV
                "Dev";
#else
                "Release";
#endif
            Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
            {
                Path = "\\System\\Policies\\Posture", Name = "Build posture", Type = "Policy",
                Owner = "\\System", Integrity = "System", StartType = "manual", Enabled = true,
                ManifestJson = JsonSerializer.Serialize(new
                {
                    version = 1, kind = "policy", posture,
                    note = "AdbElevation ceiling: a Release build compiles the self-elevation path out entirely.",
                }),
            });
        }
        catch (Exception ex) { Subsystem.Dg.Log("registrar", "posture seed failed: " + ex.Message); }

        // --- Cards as Cm objects (\Capability\Card\*, REGISTRY-SPEC §3 kind:"card") — the Surface's
        //     widget defs. SEED-IF-ABSENT (like models): the user and the agent EDIT and MINT cards at
        //     runtime, so an existing record is never overwritten back to the seed, and "Card" must
        //     never join any reconcile drop-list — agent-authored cards have no file behind them. ---
        try
        {
            var cardsJson = ObpHost.ReadAllText("shell/cards.json")
                ?? throw new System.IO.FileNotFoundException("cards.json");
            using var cdoc = JsonDocument.Parse(cardsJson);
            foreach (var c in cdoc.RootElement.EnumerateArray())
            {
                try
                {
                    var cid = c.TryGetProperty("id", out var civ) ? (civ.GetString() ?? "") : "";
                    if (cid.Length == 0) continue;
                    var cp = "\\Capability\\Card\\" + cid;
                    if (Subsystem.Cm.Cm.Get(cp) != null) continue;   // seed-if-absent — runtime edits win
                    // The §3 manifest = contract defaults + the seed entry's properties (entry wins;
                    // JsonElement values serialize verbatim through System.Text.Json).
                    var m = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["version"] = 1, ["kind"] = "card", ["consumers"] = new[] { "canvas" },
                    };
                    foreach (var prop in c.EnumerateObject()) m[prop.Name] = prop.Value;
                    Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                    {
                        Path = cp, Name = cid, Type = "Card", Owner = "\\Shell", Integrity = "User",
                        StartType = "manual", Enabled = true, ManifestJson = JsonSerializer.Serialize(m),
                    });
                }
                catch (Exception exOne) { Subsystem.Dg.Log("registrar", "card seed entry failed: " + exOne.Message); }
            }
        }
        catch (Exception ex) { Subsystem.Dg.Log("registrar", "cards.json seed failed: " + ex.Message); }

        // --- Models as Cm objects (\Capability\Model\*) — the ONE truth ModelCatalog projects. The known,
        //     downloadable set is DATA (shell/models.json), seeded-if-absent so a user/agent selection or a
        //     disk-discovered record survives reboots. ModelCatalog auto-discovers sideloaded files. No
        //     model file or URL is a C# literal (SS020) — the list lives in data, like tools/prompts/cards.
        //     (Kokoro TTS is a DISABLED onnx scaffold; lib/speech.js gates on the model record + the lane
        //     mount below and stays silent until both are enabled.) ---
        try
        {
            var modelsJson = ObpHost.ReadAllText("shell/models.json")
                ?? throw new System.IO.FileNotFoundException("models.json");
            using var mdoc = JsonDocument.Parse(modelsJson);
            foreach (var m in mdoc.RootElement.EnumerateArray())
            {
                var mid = m.TryGetProperty("id", out var miv) ? (miv.GetString() ?? "") : "";
                if (mid.Length == 0 || mid.StartsWith("_")) continue;          // skip the _about meta entry
                var mp = "\\Capability\\Model\\" + mid;
                var mname = m.TryGetProperty("name", out var mnv) ? (mnv.GetString() ?? mid) : mid;
                var menabled = m.TryGetProperty("enabled", out var mev) && mev.ValueKind == JsonValueKind.True;
                // models.json is the authoritative IMPORT SOURCE for known-model METADATA (url, minBytes,
                // sampler, modality…); the registry owns the two pieces of STATE over the file — the user's
                // enable SELECTION and any runtime `degraded` mark. Refresh metadata on every boot while
                // preserving that state, so a models.json fix (a corrected url, a new `sampler` contract)
                // reaches an already-seeded device, yet a selection still survives reboots.
                var existing = Subsystem.Cm.Cm.Get(mp);
                bool menabledFinal = existing != null ? existing.Enabled : menabled;
                string manifestJson = m.GetRawText();
                if (existing?.ManifestJson != null)
                {
                    try
                    {
                        if (System.Text.Json.Nodes.JsonNode.Parse(existing.ManifestJson) is System.Text.Json.Nodes.JsonObject oldObj
                            && oldObj["degraded"] is System.Text.Json.Nodes.JsonNode deg
                            && System.Text.Json.Nodes.JsonNode.Parse(manifestJson) is System.Text.Json.Nodes.JsonObject newObj)
                        {
                            newObj["degraded"] = deg.DeepClone();
                            manifestJson = newObj.ToJsonString();
                        }
                    }
                    catch (Exception ex) { Subsystem.Dg.Log("registrar", $"model {mid} degraded-carry failed: {ex.Message}"); }
                }
                Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
                {
                    Path = mp, Name = mname, Type = "Model", Owner = "\\Agent", Integrity = "User",
                    StartType = "manual", Enabled = menabledFinal, ManifestJson = manifestJson,
                });
            }
        }
        catch (Exception ex) { Subsystem.Dg.Log("registrar", "models.json seed failed: " + ex.Message); }
        // TTS is now the native ORT-free dpx `ss tts` verb (CRQ121). The WebView ort-web Kokoro lane
        // (\Capability\Speech\Kokoro + lib/speech.js + vendor/kokoro) was removed 2026-06-24, and vendor/ort
        // (onnxruntime-web, 31MB) followed once orphaned — ONNX inferencing is dpx, home-rolled; no foreign runtime.

        // --- Quick-assist config (the power-button / assist-gesture panel). Stored as a Cm object so its
        //     actions are registry-driven, not hardcoded. Native hookup (assist intent) lands when the
        //     device is back; the config + the actions list live here now. ---
        Reg("\\Capability\\QuickAssist\\default", "Quick Assist", "QuickAssist", new
        {
            version = 1, id = "default", trigger = "assist-gesture",
            note = "Long-press power / assist gesture opens the agent in quick-assist mode.",
            actions = new object[]
            {
                new { label = "Ask Subsystem", kind = "open-agent" },
                new { label = "Battery", kind = "tool", tool = "get_battery" },
                new { label = "Flashlight", kind = "tool", tool = "set_flashlight", args = new { state = "Toggle" } },
                new { label = "Screenshot", kind = "command", command = "Get-Screenshot" },
            }
        });

        Subsystem.Dg.Log("registrar", "seeded agent objects (tools/models/quick-assist)");
    }
}

/// ═══════════════════════════════════════════════════════════════════════════════
/// IMMEDIATE-MODE HTML — THE TAO OF DATASTAR
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// This dashboard follows the "Tao of Datastar" philosophy:
///   https://data-star.dev/essays/tao_of_datastar
///
/// CORE PRINCIPLE: The server renders the ENTIRE page from state on every push.
/// One morph. One <div id="main">. Datastar diffs the DOM.
///
/// Think of it as "immediate mode" rendering for HTML — just like a game engine
/// redraws every frame from state, we re-render every dashboard element from the
/// current Elm model on every state change. The server is the source of truth.
/// The client is a thin display layer.
///
/// WHY THIS MATTERS:
/// - No stale fragments: every push is the complete, consistent view
/// - No element-targeting bugs: we don't guess which elements changed
/// - No Datastar PatchElementsNoTargetsFound errors (we bypass element patches)
/// - Trivially correct: if the render function is right, the UI is right
/// - Version, theme, session status, output — ALL update in one atomic morph
///
/// WHAT THIS MEANS IN PRACTICE:
/// - renderMainContent: composes ALL dynamic content into <div id="main">
/// - pushState: calls renderMainContent once, sends one SSE morph
/// - renderShell: provides only the static HTML skeleton (head, scripts, CSS)
///   plus an empty <div id="main"></div> placeholder and the SSE data-init
/// - ALL state flows through the single SSE morph — never add per-element patches
///
/// DO NOT DIVERGE FROM THIS PATTERN. There is no reason to. If you think you
/// need per-element patches, you are wrong. Re-read the Tao of Datastar essay.
/// ═══════════════════════════════════════════════════════════════════════════════
module SageFs.Server.Dashboard

open System
open System.IO
open Falco
open Falco.Markup
open Falco.Routing
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open System.Text.RegularExpressions
open SageFs
open SageFs.Affordances
open SageFs.Utils

module FalcoResponse = Falco.Response

let defaultThemeName = "Kanagawa"

/// Precomputed syntax-color RGB → CSS class lookup (eliminates 12-branch if/elif chain)
let syntaxColorLookup =
  let t = Theme.defaults
  dict [
    Theme.hexToRgb t.SynKeyword, "syn-keyword"
    Theme.hexToRgb t.SynString, "syn-string"
    Theme.hexToRgb t.SynComment, "syn-comment"
    Theme.hexToRgb t.SynNumber, "syn-number"
    Theme.hexToRgb t.SynOperator, "syn-operator"
    Theme.hexToRgb t.SynType, "syn-type"
    Theme.hexToRgb t.SynFunction, "syn-function"
    Theme.hexToRgb t.SynModule, "syn-module"
    Theme.hexToRgb t.SynAttribute, "syn-attribute"
    Theme.hexToRgb t.SynPunctuation, "syn-punctuation"
    Theme.hexToRgb t.SynConstant, "syn-constant"
    Theme.hexToRgb t.SynProperty, "syn-property"
  ]

/// Save theme preferences to ~/.SageFs/themes.json
let saveThemes (sageFsDir: string) (themes: Collections.Concurrent.ConcurrentDictionary<string, string>) =
  try
    match Directory.Exists sageFsDir with
    | false -> Directory.CreateDirectory sageFsDir |> ignore
    | true -> ()
    let path = Path.Combine(sageFsDir, "themes.json")
    let dict = themes |> Seq.map (fun kv -> kv.Key, kv.Value) |> dict
    let json = Text.Json.JsonSerializer.Serialize(dict, Text.Json.JsonSerializerOptions(WriteIndented = true))
    File.WriteAllText(path, json)
  with ex -> Log.warn "Failed to save themes to %s: %s" sageFsDir ex.Message

/// Load theme preferences from ~/.SageFs/themes.json
let loadThemes (sageFsDir: string) : Collections.Concurrent.ConcurrentDictionary<string, string> =
  let result = Collections.Concurrent.ConcurrentDictionary<string, string>()
  try
    let path = Path.Combine(sageFsDir, "themes.json")
    match File.Exists(path) with
    | true ->
      let json = File.ReadAllText(path)
      let dict = Text.Json.JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, string>>(json)
      match isNull dict with
      | false ->
        for kv in dict do
          result.[kv.Key] <- kv.Value
      | true -> ()
    | false -> ()
  with ex -> Log.warn "Failed to load themes from %s: %s" sageFsDir ex.Message
  result

/// sseHtmlElements uses renderHtml which prepends <!DOCTYPE html> to every
/// fragment, causing Datastar to choke. Use renderNode + sseStringElements instead.
let ssePatchNode (ctx: HttpContext) (node: XmlNode) =
  Response.sseStringElements ctx (renderNode node)

/// Discriminated union for output line kinds — replaces stringly-typed matching.
type OutputLineKind =
  | ResultLine
  | ErrorLine
  | InfoLine
  | SystemLine

module OutputLineKind =
  let fromString (s: string) =
    match s.ToLowerInvariant() with
    | "result" -> ResultLine
    | "error" -> ErrorLine
    | "info" -> InfoLine
    | _ -> SystemLine

  let toCssClass = function
    | ResultLine -> "output-result"
    | ErrorLine -> "output-error"
    | InfoLine -> "output-info"
    | SystemLine -> "output-system"

/// Parsed output line with typed kind.
type OutputLine = {
  Timestamp: string option
  Kind: OutputLineKind
  Text: string
}

/// Discriminated union for diagnostic severity.
type DiagSeverity =
  | DiagError
  | DiagWarning

module DiagSeverity =
  let fromString (s: string) =
    match s.ToLowerInvariant() with
    | "error" -> DiagError
    | _ -> DiagWarning

  let toCssClass = function
    | DiagError -> "diag-error"
    | DiagWarning -> "diag-warning"

  let toIcon = function
    | DiagError -> "✗"
    | DiagWarning -> "⚠"

/// Parsed diagnostic with typed severity.
type Diagnostic = {
  Severity: DiagSeverity
  Message: string
  Line: int
  Col: int
}

/// Eval statistics view model — pre-computed for rendering.
type EvalStatsView = {
  Count: int
  AvgMs: float
  MinMs: float
  MaxMs: float
}

/// Discover .fsproj and .sln/.slnx files in a directory.
type DiscoveredProjects = {
  WorkingDir: string
  Solutions: string list
  Projects: string list
}

let discoverProjects (workingDir: string) : DiscoveredProjects =
  let projects =
    try
      Directory.EnumerateFiles(workingDir, "*.fsproj", SearchOption.AllDirectories)
      |> Seq.map (fun p -> Path.GetRelativePath(workingDir, p))
      |> Seq.toList
    with _ -> []
  let solutions =
    try
      Directory.EnumerateFiles(workingDir)
      |> Seq.filter (fun f ->
        let ext = Path.GetExtension(f).ToLowerInvariant()
        ext = ".sln" || ext = ".slnx")
      |> Seq.map Path.GetFileName
      |> Seq.toList
    with _ -> []
  { WorkingDir = workingDir; Solutions = solutions; Projects = projects }

/// Render keyboard shortcut help as an HTML fragment.
let renderKeyboardHelp () =
  let shortcut key desc =
    Elem.tr [] [
      Elem.td [ Attr.style "padding: 2px 8px; font-family: monospace; color: var(--accent);" ] [ Text.raw key ]
      Elem.td [ Attr.style "padding: 2px 8px;" ] [ Text.raw desc ]
    ]
  Elem.div [ Attr.id "keyboard-help"; Attr.style "margin-top: 0.5rem;" ] [
    Elem.table [ Attr.style "font-size: 0.85rem; border-collapse: collapse;" ] [
      shortcut "Alt+Enter" "Evaluate code"
      shortcut "Tab" "Insert 2 spaces (in editor)"
      shortcut "Ctrl+L" "Clear output"
    ]
  ]

/// Generate a JS object literal mapping theme names → CSS variable strings.
let themePresetsJs () =
  let entries =
    ThemePresets.all
    |> List.map (fun (name, config) ->
      sprintf "  %s: `%s`" (System.Text.Json.JsonSerializer.Serialize(name)) (Theme.toCssVariables config))
  sprintf "{\n%s\n}" (String.concat ",\n" entries)

/// Render a <style id="theme-vars"> element with CSS variables for the given theme.
/// Pushed via SSE on session switch — Datastar morphs the existing style element.
let renderThemeVars (themeName: string) =
  let config =
    ThemePresets.all
    |> List.tryFind (fun (n, _) -> n = themeName)
    |> Option.map snd
    |> Option.defaultValue Theme.defaults
  Elem.style [ Attr.id "theme-vars" ] [
    Text.raw (sprintf ":root { %s }" (Theme.toCssVariables config))
  ]

/// Render a <select id="theme-picker"> with the correct option selected.
/// Pushed via SSE on session switch — Datastar morphs the existing picker.
let renderThemePicker (selectedTheme: string) =
  Elem.select
    [ Attr.id "theme-picker"; Attr.class' "theme-select" ]
    (ThemePresets.all |> List.map (fun (name, _) ->
      Elem.option
        ([ Attr.value name ] @ (match name = selectedTheme with | true -> [ Attr.create "selected" "selected" ] | false -> []))
        [ Text.raw name ]))

/// Render the dashboard HTML shell.
/// Datastar initializes and connects to the /dashboard/stream SSE endpoint.
let renderShell (version: string) =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ Text.raw "SageFs Dashboard" ]
      // Connection monitor: intercept fetch to detect SSE stream lifecycle.
      // Banner is ONLY for problems — hidden by default, shown on failure.
      Elem.script [] [ Text.raw """
        (function() {
          var origFetch = window.fetch, pollTimer = null;
          function showProblem(text) {
            var b = document.getElementById('server-status');
            if (b) { b.className = 'conn-banner conn-disconnected'; b.textContent = text; b.style.display = ''; }
            startPolling();
          }
          function hideBanner() {
            var b = document.getElementById('server-status');
            if (b) { b.style.display = 'none'; }
            stopPolling();
          }
          function startPolling() {
            if (pollTimer) return;
            pollTimer = setInterval(function() {
              origFetch('/api/daemon-info').then(function(r) {
                if (r.ok) hideBanner();
              }).catch(function() {});
            }, 2000);
          }
          function stopPolling() {
            if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
          }
          window.fetch = function(url) {
            var isStream = typeof url === 'string' && url.indexOf('/dashboard/stream') !== -1;
            var p = origFetch.apply(this, arguments);
            if (isStream) {
              p.then(function(resp) {
                if (resp.ok) hideBanner();
                else showProblem('\u274c Server error (' + resp.status + ')');
              }).catch(function() {
                showProblem('\u274c Server disconnected \u2014 waiting for reconnect...');
              });
            }
            return p;
          };
        })();
      """ ]
      Ds.cdnScript
      Elem.style [] [ Text.raw """
        :root { --font-size: 14px; --sidebar-width: 320px; }
        * { box-sizing: border-box; margin: 0; padding: 0; }""" ]
      Elem.style [] [ Text.raw """
        body { font-family: 'Cascadia Code', 'Fira Code', monospace; background: var(--bg-default); color: var(--fg-default); font-size: var(--font-size); height: 100vh; display: flex; flex-direction: column; overflow: hidden; }
        h1 { color: var(--fg-blue); font-size: 1.4rem; }
        .app-header { display: flex; align-items: center; justify-content: space-between; padding: 0.5rem 1rem; border-bottom: 1px solid var(--border-normal); flex-shrink: 0; }
        .app-layout { display: flex; flex: 1; overflow: hidden; }
        .main-area { flex: 1; display: flex; flex-direction: column; overflow: hidden; min-width: 0; }
        .sidebar { width: var(--sidebar-width); border-left: 1px solid var(--border-normal); background: var(--bg-panel); overflow-y: auto; flex-shrink: 0; transition: width 0.2s, padding 0.2s; }
        .sidebar.collapsed { width: 0; padding: 0; overflow: hidden; border-left: none; }
        .sidebar-inner { padding: 0.75rem; display: flex; flex-direction: column; gap: 0.75rem; min-width: var(--sidebar-width); }
        .panel { background: var(--bg-panel); border: 1px solid var(--border-normal); border-radius: 8px; padding: 0.75rem; }
        .panel h2 { color: var(--fg-blue); font-size: 0.9rem; margin-bottom: 0.5rem; border-bottom: 1px solid var(--border-normal); padding-bottom: 0.5rem; display: flex; justify-content: space-between; align-items: center; }
        .output-area { flex: 1; display: flex; flex-direction: column; overflow: hidden; border-bottom: 1px solid var(--border-normal); }
        .output-header { display: flex; align-items: center; justify-content: space-between; padding: 0.5rem 1rem; flex-shrink: 0; }
        .output-header h2 { color: var(--fg-blue); font-size: 1rem; margin: 0; }
        #output-panel { flex: 1; overflow-y: auto; scroll-behavior: smooth; background: var(--bg-default); font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 0.85rem; padding: 0.25rem 0; }
        .eval-area { flex-shrink: 0; padding: 0.75rem 1rem; }
        .eval-area summary { list-style: none; user-select: none; }
        .eval-area summary::-webkit-details-marker { display: none; }
        .eval-area[open] summary span:first-child { }
        .eval-area summary span:first-child::before { content: ''; }
        #editor-area { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
        #session-picker:empty { display: none; }
        #session-picker:not(:empty) ~ #editor-area { display: none; }
        .picker-container { display: flex; flex-direction: column; align-items: center; justify-content: center; flex: 1; padding: 2rem; gap: 1.5rem; }
        .picker-container h2 { color: var(--fg-blue); font-size: 1.4rem; margin-bottom: 0.5rem; }
        .picker-options { display: flex; gap: 1rem; flex-wrap: wrap; justify-content: center; width: 100%; max-width: 900px; }
        .picker-card { flex: 1; min-width: 240px; background: var(--bg-panel); border: 1px solid var(--border-normal); border-radius: 8px; padding: 1.25rem; cursor: pointer; transition: border-color 0.15s, background 0.15s; }
        .picker-card:hover { border-color: var(--fg-blue); background: var(--bg-highlight); }
        .picker-card h3 { color: var(--fg-blue); font-size: 1rem; margin-bottom: 0.5rem; }
        .picker-card p { color: var(--fg-dim); font-size: 0.85rem; line-height: 1.4; }
        .picker-form { width: 100%; max-width: 500px; }
        .picker-form .eval-input { min-height: auto; height: 2rem; margin-bottom: 0.5rem; }
        .picker-previous { width: 100%; max-width: 600px; }
        .picker-session-row { display: flex; align-items: center; justify-content: space-between; padding: 0.5rem 0.75rem; border-bottom: 1px solid var(--border-normal); cursor: pointer; border-radius: 4px; transition: background 0.1s; }
        .picker-session-row:hover { background: var(--bg-highlight); }
        .status { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; font-weight: bold; }
        .status-ready { background: var(--fg-green); color: var(--bg-default); }
        .status-warming { background: var(--fg-yellow); color: var(--bg-default); }
        .status-faulted { background: var(--fg-red); color: white; }
        .output-line { font-size: 0.85rem; padding: 1px 0.5rem; white-space: pre-wrap; word-break: break-all; line-height: 1.5; }
        .output-result { color: var(--fg-green); }
        .output-error { color: var(--fg-red); }
        .output-info { color: var(--fg-blue); }
        .output-system { color: var(--fg-dim); }
        .diag { font-size: 0.85rem; padding: 1px 0.5rem; line-height: 1.5; }
        .diag-error { color: var(--fg-red); }
        .diag-warning { color: var(--fg-yellow); }
        .diag-location { font-family: monospace; background: var(--bg-selection); padding: 1px 4px; border-radius: 3px; font-size: 0.8rem; margin-right: 0.25rem; }
        .meta { color: var(--fg-dim); font-size: 0.8rem; }
        .eval-input { width: 100%; background: var(--bg-editor); color: var(--fg-default); border: 1px solid var(--border-normal); border-radius: 4px; padding: 0.5rem; font-family: inherit; font-size: 0.9rem; resize: vertical; min-height: 80px; tab-size: 2; }
        .eval-input:focus { outline: 1px solid var(--border-focus); border-color: var(--border-focus); }
        .eval-btn { background: var(--fg-blue); color: var(--bg-default); border: none; border-radius: 4px; padding: 0.5rem 1rem; cursor: pointer; font-family: inherit; font-weight: bold; margin-top: 0.5rem; transition: opacity 0.15s; }
        .eval-btn:hover { opacity: 0.85; }
        .eval-btn:active { opacity: 0.7; }
        .session-btn { background: var(--border-normal); color: var(--fg-default); border: none; border-radius: 4px; padding: 2px 8px; cursor: pointer; font-size: 0.8rem; transition: background 0.15s; }
        .session-btn:hover { background: var(--fg-blue); color: var(--bg-default); }
        .session-btn-danger:hover { background: var(--fg-red); color: white; }
        .session-selected { background: var(--bg-selection); border-left: 3px solid var(--border-focus); }
        .session-row:hover { background: var(--bg-selection); }
        .panel-header-btn { background: none; border: 1px solid var(--border-normal); color: var(--fg-default); border-radius: 4px; padding: 1px 8px; cursor: pointer; font-size: 0.75rem; font-family: inherit; }
        .panel-header-btn:hover { background: var(--border-normal); }
        .log-box { background: var(--bg-default); border: 1px solid var(--border-normal); border-radius: 4px; padding: 0.5rem 0; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 0.85rem; }
        .conn-banner { padding: 4px 1rem; text-align: center; font-size: 0.8rem; font-weight: bold; border-radius: 0; transition: all 0.3s; flex-shrink: 0; }
        .conn-connected { background: var(--fg-green); color: var(--bg-default); }
        .conn-disconnected { background: var(--fg-red); color: white; animation: pulse 1.5s infinite; }
        .conn-reconnecting { background: var(--fg-yellow); color: var(--bg-default); }
        @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.6; } }
        .eval-btn:disabled { opacity: 0.5; cursor: not-allowed; }
        .sidebar-toggle { background: none; border: 1px solid var(--border-normal); color: var(--fg-default); border-radius: 4px; padding: 2px 8px; cursor: pointer; font-size: 0.85rem; font-family: inherit; }
        .sidebar-toggle:hover { background: var(--border-normal); }
        .resize-handle { width: 4px; background: var(--border-normal); cursor: col-resize; flex-shrink: 0; transition: background 0.15s; }
        .resize-handle:hover, .resize-handle.dragging { background: var(--border-focus); }
        .theme-select { width: 100%; background: var(--bg-editor); color: var(--fg-default); border: 1px solid var(--border-normal); border-radius: 4px; padding: 4px 8px; font-family: inherit; font-size: 0.85rem; cursor: pointer; }
        .theme-select:focus { outline: 1px solid var(--border-focus); border-color: var(--border-focus); }
        .syn-keyword { color: var(--syn-keyword); }
        .syn-string { color: var(--syn-string); }
        .syn-comment { color: var(--syn-comment); font-style: italic; }
        .syn-number { color: var(--syn-number); }
        .syn-operator { color: var(--syn-operator); }
        .syn-type { color: var(--syn-type); }
        .syn-function { color: var(--syn-function); }
        .syn-variable { color: var(--syn-variable); }
        .syn-punctuation { color: var(--syn-punctuation); }
        .syn-constant { color: var(--syn-constant); }
        .syn-module { color: var(--syn-module); }
        .syn-attribute { color: var(--syn-attribute); }
        .syn-directive { color: var(--syn-directive); }
        .syn-property { color: var(--syn-property); }
        @media (max-width: 768px) {
          .sidebar { position: fixed; right: 0; top: 0; bottom: 0; z-index: 10; }
          .sidebar.collapsed { width: 0; }
        }
      """ ]
    ]
    Elem.body [ Ds.safariStreamingFix ] [
      // Dedicated init element: connects to SSE stream, defines Datastar signals.
      // This is the ONLY Datastar-aware element in the shell — everything else
      // arrives via the full-page morph on /dashboard/stream.
      Elem.div [ Ds.onInit (Ds.get "/dashboard/stream"); Ds.signal ("helpVisible", "false"); Ds.signal ("sidebarOpen", "true"); Ds.signal ("sessionId", ""); Ds.signal ("code", ""); Ds.signal ("newSessionDir", ""); Ds.signal ("manualProjects", "") ] []
      // Connection status banner — hidden by default, shown only on problems
      Elem.div [ Attr.id "server-status"; Attr.class' "conn-banner conn-disconnected"; Attr.style "display:none" ] [
        Text.raw "⏳ Connecting to server..."
      ]
      // Full-page morph target — renderMainContent pushes the entire UI here.
      // Initial load is empty; first SSE push fills it with complete content.
      Elem.div [ Attr.id "main" ] [
        Elem.div [ Attr.style "display: flex; align-items: center; justify-content: center; height: 100vh; color: var(--fg-dim);" ] [
          Text.raw "⏳ Loading dashboard..."
        ]
      ]
      // Auto-scroll output panel to bottom when new content arrives
      Elem.script [] [ Text.raw """
        new MutationObserver(function() {
          var panel = document.getElementById('output-panel');
          if (panel) panel.scrollTop = panel.scrollHeight;
        }).observe(document.getElementById('main') || document.body, { childList: true, subtree: true });
      """ ]
      // Theme picker: update style element on selection change, notify server
      // Uses event delegation so handler survives Datastar DOM morphing
      Elem.script [] [ Text.raw (sprintf """
        (function() {
          var themes = %s;
          document.addEventListener('change', function(e) {
            if (e.target.id !== 'theme-picker') return;
            var css = themes[e.target.value];
            if (!css) return;
            var styleEl = document.getElementById('theme-vars');
            if (styleEl) styleEl.textContent = ':root { ' + css + ' }';
            fetch('/dashboard/set-theme', {
              method: 'POST',
              headers: {'Content-Type': 'application/json'},
              body: JSON.stringify({theme: e.target.value})
            });
          });
        })();
      """ (themePresetsJs ())) ]
      // Details toggle: update arrow indicator when eval section opens/closes
      Elem.script [] [ Text.raw """
        document.addEventListener('toggle', function(e) {
          if (e.target.id !== 'evaluate-section') return;
          var label = e.target.querySelector('summary span:first-child');
          if (label) label.textContent = e.target.open ? '\u25be Evaluate' : '\u25b8 Evaluate';
        }, true);
      """ ]
      // Font size adjustment: Ctrl+= / Ctrl+- changes --font-size CSS variable
      Elem.script [] [ Text.raw """
        (function() {
          var sizes = [10, 12, 14, 16, 18, 20, 24];
          var idx = 2;
          document.addEventListener('keydown', function(e) {
            if (e.ctrlKey && (e.key === '=' || e.key === '+')) { e.preventDefault(); idx = Math.min(sizes.length - 1, idx + 1); document.documentElement.style.setProperty('--font-size', sizes[idx] + 'px'); }
            if (e.ctrlKey && e.key === '-') { e.preventDefault(); idx = Math.max(0, idx - 1); document.documentElement.style.setProperty('--font-size', sizes[idx] + 'px'); }
            if (e.ctrlKey && e.key === 'Tab') {
              e.preventDefault();
              var body = {action: e.shiftKey ? 'sessionCyclePrev' : 'sessionCycleNext'};
              fetch('/api/dispatch', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body) });
              return;
            }
            // Session navigation when not typing in an input/textarea
            var tag = (e.target.tagName || '').toLowerCase();
            if (tag !== 'input' && tag !== 'textarea') {
              var action = null;
              var value = null;
              if (e.key === 'j' || e.key === 'ArrowDown') { action = 'sessionNavDown'; }
              if (e.key === 'k' || e.key === 'ArrowUp') { action = 'sessionNavUp'; }
              if (e.key === 'Enter') { action = 'sessionSelect'; }
              if (e.key === 'x' || e.key === 'Delete') { action = 'sessionDelete'; }
              if (e.key === 'X') { action = 'sessionStopOthers'; }
              if (e.key === 'n') { e.preventDefault(); fetch('/dashboard/session/create', {method:'POST'}); return; }
              if (e.key >= '1' && e.key <= '9') { action = 'sessionSetIndex'; value = String(parseInt(e.key) - 1); }
              if (action) {
                e.preventDefault();
                var body = value ? {action: action, value: value} : {action: action};
                fetch('/api/dispatch', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body) });
              }
            }
          });
          // Sidebar resize drag
          var handle = document.getElementById('sidebar-resize');
          var sidebar = document.getElementById('sidebar');
          if (handle && sidebar) {
            var dragging = false;
            handle.addEventListener('mousedown', function(e) {
              dragging = true; handle.classList.add('dragging');
              e.preventDefault();
            });
            document.addEventListener('mousemove', function(e) {
              if (!dragging) return;
              var w = Math.max(200, Math.min(600, window.innerWidth - e.clientX));
              sidebar.style.width = w + 'px';
            });
            document.addEventListener('mouseup', function() {
              if (dragging) { dragging = false; handle.classList.remove('dragging'); }
            });
          }
        })();
      """ ]
    ]
  ]

/// Render session status as an HTML fragment for Datastar morphing.
let renderSessionStatus (sessionState: string) (sessionId: string) (workingDir: string) (warmupProgress: string) =
  let warmupNode =
    match warmupProgress.Length > 0 with
    | true ->
      [ Elem.br []
        Elem.span [ Attr.class' "meta warmup-progress" ] [
          Text.raw (sprintf "⏳ %s" warmupProgress)
        ] ]
    | false -> []
  match sessionState with
  | "Ready" ->
    Elem.div [ Attr.id "session-status"; Attr.create "data-working-dir" workingDir ] [
      yield Elem.span [ Attr.class' "status status-ready" ] [ Text.raw sessionState ]
      yield Elem.br []
      yield Elem.span [ Attr.class' "meta" ] [
        Text.raw (sprintf "Session: %s | CWD: %s" sessionId workingDir)
      ]
      yield! warmupNode
    ]
  | _ ->
    let statusClass =
      match sessionState with
      | "WarmingUp" -> "status-warming"
      | _ -> "status-faulted"
    Elem.div [ Attr.id "session-status"; Attr.create "data-working-dir" workingDir ] [
      yield Elem.span [ Attr.class' (sprintf "status %s" statusClass) ] [ Text.raw sessionState ]
      yield Elem.br []
      yield Elem.span [ Attr.class' "meta" ] [
        Text.raw (sprintf "Session: %s | CWD: %s" sessionId workingDir)
      ]
      yield! warmupNode
    ]

/// Render eval stats as an HTML fragment.
let renderEvalStats (stats: EvalStatsView) =
  Elem.div [ Attr.id "eval-stats"; Attr.class' "meta" ] [
    Text.raw (sprintf "%d evals · avg %.0fms · min %.0fms · max %.0fms" stats.Count stats.AvgMs stats.MinMs stats.MaxMs)
  ]

/// Map a tree-sitter capture name to the CSS class suffix.
let captureToCssClass (capture: string) =
  match capture with
  | s when s.StartsWith("keyword", System.StringComparison.Ordinal) -> "syn-keyword"
  | s when s.StartsWith("string", System.StringComparison.Ordinal) -> "syn-string"
  | s when s.StartsWith("comment", System.StringComparison.Ordinal) -> "syn-comment"
  | s when s.StartsWith("number", System.StringComparison.Ordinal) -> "syn-number"
  | s when s.StartsWith("operator", System.StringComparison.Ordinal) -> "syn-operator"
  | s when s.StartsWith("type", System.StringComparison.Ordinal) -> "syn-type"
  | s when s.StartsWith("function", System.StringComparison.Ordinal) -> "syn-function"
  | s when s.StartsWith("variable", System.StringComparison.Ordinal) -> "syn-variable"
  | s when s.StartsWith("punctuation", System.StringComparison.Ordinal) -> "syn-punctuation"
  | s when s.StartsWith("constant", System.StringComparison.Ordinal) -> "syn-constant"
  | s when s.StartsWith("module", System.StringComparison.Ordinal) -> "syn-module"
  | s when s.StartsWith("attribute", System.StringComparison.Ordinal) -> "syn-attribute"
  | s when s.StartsWith("property", System.StringComparison.Ordinal) -> "syn-property"
  | s when s.StartsWith("boolean", System.StringComparison.Ordinal) -> "syn-constant"
  | _ -> ""

/// Render a single line of code with syntax highlighting as HTML spans.
let renderHighlightedLine (spans: ColorSpan array) (line: string) : XmlNode list =
  match spans.Length = 0 || line.Length = 0 with
  | true -> [ Text.raw (System.Net.WebUtility.HtmlEncode line) ]
  | false ->
    let nodes = ResizeArray<XmlNode>()
    let mutable pos = 0
    for span in spans do
      match span.Start < pos with
      | true -> ()
      | false ->
      match span.Start > pos && pos < line.Length with
      | true ->
        let gapEnd = min span.Start line.Length
        nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode(line.Substring(pos, gapEnd - pos))))
        pos <- gapEnd
      | false -> ()
      match span.Start >= 0 && span.Start < line.Length with
      | true ->
        let end' = min (span.Start + span.Length) line.Length
        let text = line.Substring(span.Start, end' - span.Start)
        // Map fg packed RGB to a CSS class using precomputed lookup table
        let cssClass =
          match syntaxColorLookup.TryGetValue(span.Fg) with
          | true, cls -> cls
          | false, _ -> ""
        match cssClass <> "" with
        | true ->
          nodes.Add(Elem.span [ Attr.class' cssClass ] [ Text.raw (System.Net.WebUtility.HtmlEncode text) ])
        | false ->
          nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode text))
        pos <- end'
      | false -> ()
    match pos < line.Length with
    | true ->
      nodes.Add(Text.raw (System.Net.WebUtility.HtmlEncode(line.Substring(pos))))
    | false -> ()
    nodes |> Seq.toList

/// Render output lines as an HTML fragment.
let renderOutput (lines: OutputLine list) =
  Elem.div [ Attr.id "output-panel" ] [
    match lines.IsEmpty with
    | true ->
      Elem.span [ Attr.class' "meta" ] [ Text.raw "No output yet" ]
    | false ->
      yield! lines |> List.map (fun line ->
        let css = OutputLineKind.toCssClass line.Kind
        Elem.div [ Attr.class' (sprintf "output-line %s" css) ] [
          match line.Timestamp with
          | Some t ->
            Elem.span [ Attr.class' "meta"; Attr.style "margin-right: 0.5rem;" ] [
              Text.raw t
            ]
          | None -> ()
          match (line.Kind = ResultLine || line.Kind = InfoLine) && SyntaxHighlight.isAvailable () with
          | true ->
            let allSpans = SyntaxHighlight.tokenize Theme.defaults line.Text
            match allSpans.Length > 0 with
            | true -> yield! renderHighlightedLine allSpans.[0] line.Text
            | false -> Text.raw (System.Net.WebUtility.HtmlEncode line.Text)
          | false ->
            Text.raw (System.Net.WebUtility.HtmlEncode line.Text)
        ])
  ]

/// Render diagnostics as an HTML fragment.
let renderDiagnostics (diags: Diagnostic list) =
  Elem.div [ Attr.id "diagnostics-panel"; Attr.class' "log-box" ] [
    match diags.IsEmpty with
    | true ->
      Elem.span [ Attr.class' "meta" ] [ Text.raw "No diagnostics" ]
    | false ->
      yield! diags |> List.map (fun diag ->
        let cls = DiagSeverity.toCssClass diag.Severity
        Elem.div [ Attr.class' (sprintf "diag %s" cls) ] [
          Elem.span [ Attr.style "margin-right: 0.25rem;" ] [
            Text.raw (DiagSeverity.toIcon diag.Severity)
          ]
          match diag.Line > 0 || diag.Col > 0 with
          | true ->
            Elem.span [ Attr.class' "diag-location" ] [
              Text.raw (sprintf "L%d:%d" diag.Line diag.Col)
            ]
          | false -> ()
          Elem.span [] [
            Text.raw (sprintf " %s" diag.Message)
          ]
        ])
  ]

type ParsedSession = {
  Id: string
  Status: string
  StatusMessage: string option
  IsActive: bool
  IsSelected: bool
  ProjectsText: string
  EvalCount: int
  Uptime: string
  WorkingDir: string
  LastActivity: string
}

let parseSessionLines (content: string) =
  let sessionRegex = Regex(@"^([> ])\s+(\S+)\s*\[([^\]]+)\](\s*\*)?(\s*\([^)]*\))?(\s*evals:\d+)?(\s*up:(?:just now|\S+))?(\s*dir:\S.*?)?(\s*last:.+)?$")
  let extractTag (prefix: string) (value: string) =
    let v = value.Trim()
    match v.StartsWith(prefix, System.StringComparison.Ordinal) with
    | true -> v.Substring(prefix.Length).Trim()
    | false -> ""
  content.Split('\n')
  |> Array.filter (fun (l: string) ->
    l.Length > 0
    && not (l.StartsWith("───", System.StringComparison.Ordinal))
    && not (l.StartsWith("⏳", System.StringComparison.Ordinal))
    && not (l.Contains("↑↓ nav"))
    && not (l.Contains("Enter switch"))
    && not (l.Contains("Ctrl+Tab cycle")))
  |> Array.map (fun (l: string) ->
    let m = sessionRegex.Match(l)
    match m.Success with
    | true ->
      let evalsMatch = Regex.Match(m.Groups.[6].Value, @"evals:(\d+)")
      { Id = m.Groups.[2].Value
        Status = m.Groups.[3].Value
        StatusMessage = None
        IsActive = m.Groups.[4].Value.Contains("*")
        IsSelected = m.Groups.[1].Value = ">"
        ProjectsText = m.Groups.[5].Value.Trim()
        EvalCount = match evalsMatch.Success with | true -> int evalsMatch.Groups.[1].Value | false -> 0
        Uptime = extractTag "up:" m.Groups.[7].Value
        WorkingDir = extractTag "dir:" m.Groups.[8].Value
        LastActivity = extractTag "last:" m.Groups.[9].Value }
    | false ->
      { Id = l.Trim()
        Status = "unknown"
        StatusMessage = None
        IsActive = false
        IsSelected = false
        ProjectsText = ""
        EvalCount = 0
        Uptime = ""
        WorkingDir = ""
        LastActivity = "" })
  |> Array.toList

let isCreatingSession (content: string) =
  content.Contains("⏳ Creating session...")

/// A previously-known session that can be resumed.
type PreviousSession = {
  Id: string
  WorkingDir: string
  Projects: string list
  LastSeen: DateTime
}

/// State queries — always-present read accessors for dashboard rendering.
type DashboardQueries = {
  GetSessionState: string -> SessionState
  GetStatusMsg: string -> string option
  GetEvalStats: string -> Threading.Tasks.Task<SageFs.Affordances.EvalStats>
  GetSessionWorkingDir: string -> string
  GetActiveSessionId: unit -> string
  GetElmRegions: unit -> RenderRegion list option
  GetPreviousSessions: unit -> Threading.Tasks.Task<PreviousSession list>
  GetAllSessions: unit -> Threading.Tasks.Task<WorkerProtocol.SessionInfo list>
  GetStandbyInfo: unit -> Threading.Tasks.Task<StandbyInfo>
  GetHotReloadState: string -> Threading.Tasks.Task<{| files: {| path: string; watched: bool |} list; watchedCount: int |} option>
  GetWarmupContext: string -> Threading.Tasks.Task<WarmupContext option>
  GetWarmupProgress: string -> string
  GetTestTrace: unit -> {| Timing: Features.LiveTesting.TestCycleTiming option; IsRunning: bool; Summary: Features.LiveTesting.TestSummary |} option
  GetLiveTestingStatus: unit -> string
}

/// Commands that mutate session state.
type DashboardActions = {
  EvalCode: string -> string -> Threading.Tasks.Task<Result<string, string>>
  ResetSession: string -> Threading.Tasks.Task<Result<string, string>>
  HardResetSession: string -> Threading.Tasks.Task<Result<string, string>>
  Dispatch: SageFsMsg -> unit
  SwitchSession: (string -> Threading.Tasks.Task<Result<string, string>>) option
  StopSession: (string -> Threading.Tasks.Task<Result<string, string>>) option
  CreateSession: (string list -> string -> Threading.Tasks.Task<Result<string, string>>) option
  ShutdownCallback: (unit -> unit) option
}

/// Infrastructure dependencies — event sources, tracking, themes.
type DashboardInfra = {
  Version: string
  StateChanged: IEvent<DaemonStateChange> option
  ConnectionTracker: ConnectionTracker option
  SessionThemes: Collections.Concurrent.ConcurrentDictionary<string, string>
  GetCompletions: (string -> string -> int -> Threading.Tasks.Task<Features.AutoCompletion.CompletionItem list>) option
}

/// Render the session picker — shown in the main area when no sessions exist.
let renderSessionPicker (previous: PreviousSession list) =
  Elem.div [ Attr.id "session-picker" ] [
    Elem.div [ Attr.class' "picker-container" ] [
      Elem.h2 [] [ Text.raw "Start a Session" ]
      Elem.p [ Attr.class' "meta"; Attr.style "text-align: center; max-width: 500px;" ] [
        Text.raw "Choose how to get started. You can create a new session or resume a previous one."
      ]
      Elem.div [ Attr.class' "picker-options" ] [
        // Option 1: Create in temp directory
        Elem.div
          [ Attr.class' "picker-card"
            Ds.indicator "tempLoading"
            Ds.onClick (Ds.post "/dashboard/session/create-temp") ]
          [ Elem.h3 [] [
              Elem.span [ Ds.show "$tempLoading" ] [ Text.raw "⏳ " ]
              Elem.span [ Ds.show "!$tempLoading" ] [ Text.raw "⚡ " ]
              Text.raw "Quick Start" ]
            Elem.p [] [ Text.raw "Create a new session in a temporary directory. Good for quick experiments and throwaway work." ] ]
        // Option 2: Create in custom directory
        Elem.div [ Attr.class' "picker-card"; Attr.style "cursor: default;" ] [
          Elem.h3 [] [ Text.raw "📁 Open Directory" ]
          Elem.p [] [ Text.raw "Create a session in a specific directory with your projects." ]
          Elem.div [ Attr.class' "picker-form"; Attr.style "margin-top: 0.75rem;" ] [
            Elem.input
              [ Attr.class' "eval-input"
                Attr.style "min-height: auto; height: 2rem;"
                Ds.bind "newSessionDir"
                Attr.create "placeholder" @"C:\path\to\project" ]
            Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 0.5rem;" ] [
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                  Ds.indicator "discoverLoading"
                  Ds.attr' ("disabled", "$discoverLoading")
                  Ds.onClick (Ds.post "/dashboard/discover-projects") ]
                [ Elem.span [ Ds.show "$discoverLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$discoverLoading" ] [ Text.raw "🔍 " ]
                  Text.raw "Discover" ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                  Ds.indicator "createLoading"
                  Ds.attr' ("disabled", "$createLoading")
                  Ds.onClick (Ds.post "/dashboard/session/create") ]
                [ Elem.span [ Ds.show "$createLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$createLoading" ] [ Text.raw "➕ " ]
                  Text.raw "Create" ]
            ]
            Elem.div [ Attr.id "discovered-projects" ] []
          ]
        ]
      ]
      match previous.IsEmpty with
      | false ->
        Elem.div [ Attr.class' "picker-previous" ] [
          Elem.h3 [ Attr.style "color: var(--fg-blue); margin-bottom: 0.5rem;" ] [
            Text.raw "📋 Resume Previous"
          ]
          Elem.p [ Attr.class' "meta"; Attr.style "margin-bottom: 0.5rem;" ] [
            Text.raw "Sessions from the last 90 days. Retention is configurable."
          ]
          yield! previous |> List.map (fun s ->
            let age =
              let span = DateTime.UtcNow - s.LastSeen
              match span.TotalDays >= 1.0 with
              | true -> sprintf "%.0fd ago" span.TotalDays
              | false ->
                match span.TotalHours >= 1.0 with
                | true -> sprintf "%.0fh ago" span.TotalHours
                | false -> sprintf "%.0fm ago" span.TotalMinutes
            Elem.div
              [ Attr.class' "picker-session-row"
                Ds.onClick (Ds.post (sprintf "/dashboard/session/resume/%s" s.Id)) ]
              [ Elem.div [ Attr.style "flex: 1; min-width: 0;" ] [
                  Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5rem;" ] [
                    Elem.span [ Attr.style "font-weight: bold;" ] [ Text.raw s.Id ]
                    Elem.span [ Attr.class' "meta" ] [ Text.raw age ]
                  ]
                  match s.WorkingDir.Length > 0 with
                  | true ->
                    Elem.div
                      [ Attr.style "font-size: 0.75rem; color: var(--fg-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                        Attr.title s.WorkingDir ]
                      [ Text.raw (sprintf "📁 %s" s.WorkingDir) ]
                  | false -> ()
                  match s.Projects.IsEmpty with
                  | false ->
                    Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 2px; flex-wrap: wrap;" ] [
                      yield! s.Projects |> List.map (fun p ->
                        Elem.span
                          [ Attr.style "font-size: 0.65rem; padding: 1px 5px; border-radius: 3px; background: var(--bg-highlight); color: var(--fg-dim);" ]
                          [ Text.raw (Path.GetFileName p) ])
                    ]
                  | true -> ()
                ]
                Elem.span [ Attr.style "color: var(--fg-blue); font-size: 0.85rem;" ] [ Text.raw "▶" ]
              ])
        ]
      | true -> ()
    ]
  ]

/// Render an empty session picker (hidden — sessions exist).
let renderSessionPickerEmpty =
  Elem.div [ Attr.id "session-picker" ] []

/// Render sessions as an HTML fragment with action buttons.
let renderSessions (sessions: ParsedSession list) (creating: bool) (standbyLabel: string) =
  Elem.div [ Attr.id "sessions-panel" ] [
    match creating with
    | true ->
      Elem.div
        [ Attr.style "padding: 8px; text-align: center; color: var(--accent); font-size: 0.85rem;" ]
        [ Text.raw "⏳ Creating session..." ]
    | false -> ()
    match sessions.IsEmpty && not creating with
    | true ->
      Text.raw "No sessions"
    | false ->
      yield! sessions |> List.mapi (fun i (s: ParsedSession) ->
        let statusClass =
          match s.Status with
          | "running" -> "status-ready"
          | "starting" | "restarting" -> "status-warming"
          | _ -> "status-faulted"
        let cls =
          match s.IsSelected, s.IsActive with
          | true, _ -> "session-selected"
          | false, true -> "output-result"
          | false, false -> ""
        Elem.div
          [ Attr.class' (sprintf "session-row %s" cls)
            Attr.style "display: flex; align-items: center; justify-content: space-between; padding: 8px 0; border-bottom: 1px solid var(--border); cursor: pointer;"
            Ds.onEvent ("click", sprintf "fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'sessionSetIndex',value:'%d'})}).then(function(){fetch('/api/dispatch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'sessionSelect'})})})" i) ]
          [
            Elem.div [ Attr.style "flex: 1; min-width: 0;" ] [
              // Row 1: session ID + status + active indicator
              Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5rem;" ] [
                Elem.span [ Attr.style "font-weight: bold;" ] [ Text.raw s.Id ]
                Elem.span
                  [ Attr.class' (sprintf "status %s" statusClass)
                    Attr.style "font-size: 0.7rem; padding: 1px 6px; border-radius: 3px;" ]
                  [ Text.raw s.Status ]
                match s.StatusMessage with
                | Some msg ->
                  Elem.span
                    [ Attr.style "font-size: 0.65rem; color: var(--fg-yellow); font-style: italic;" ]
                    [ Text.raw (sprintf "⏳ %s" msg) ]
                | None -> ()
                match s.IsActive with
                | true ->
                  Elem.span [ Attr.style "color: var(--green);" ] [ Text.raw "● active" ]
                | false -> ()
                match s.Uptime.Length > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta"; Attr.style "margin-left: auto;" ] [
                    Text.raw (sprintf "⏱ %s" s.Uptime)
                  ]
                | false -> ()
              ]
              // Row 2: working directory
              match s.WorkingDir.Length > 0 with
              | true ->
                Elem.div
                  [ Attr.style "font-size: 0.75rem; color: var(--fg-dim); margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"
                    Attr.title s.WorkingDir ]
                  [ Text.raw (sprintf "📁 %s" s.WorkingDir) ]
              | false -> ()
              // Row 3: projects as tags + evals + last activity
              Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5rem; margin-top: 2px; flex-wrap: wrap;" ] [
                match s.ProjectsText.Length > 0 with
                | true ->
                  let projNames =
                    s.ProjectsText.Trim('(', ')')
                      .Split(',')
                    |> Array.map (fun p -> p.Trim())
                    |> Array.filter (fun p -> p.Length > 0)
                  yield! projNames |> Array.map (fun pName ->
                    Elem.span
                      [ Attr.style "font-size: 0.65rem; padding: 1px 5px; border-radius: 3px; background: var(--bg-highlight); color: var(--fg-dim);" ]
                      [ Text.raw pName ])
                | false -> ()
                match s.EvalCount > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta" ] [
                    Text.raw (sprintf "evals: %d" s.EvalCount)
                  ]
                | false -> ()
                match s.LastActivity.Length > 0 with
                | true ->
                  Elem.span [ Attr.class' "meta"; Attr.style "margin-left: auto;" ] [
                    Text.raw (sprintf "last: %s" s.LastActivity)
                  ]
                | false -> ()
              ]
            ]
            Elem.div [ Attr.style "display: flex; gap: 4px; margin-left: 8px;" ] [
              match s.IsActive with
              | false ->
                Elem.button
                  [ Attr.class' "session-btn"
                    Ds.onClick (Ds.post (sprintf "/dashboard/session/switch/%s" s.Id)) ]
                  [ Text.raw "⇄" ]
              | true -> ()
              Elem.button
                [ Attr.class' "session-btn session-btn-danger"
                  Ds.onClick (Ds.post (sprintf "/dashboard/session/stop/%s" s.Id)) ]
                [ Text.raw "■" ]
            ]
          ])
    Elem.div
      [ Attr.style "display: flex; justify-content: space-between; align-items: center; font-size: 0.7rem; color: var(--fg-dim); padding: 4px 0; margin-top: 4px;" ]
      [
        Elem.span [] [
          Text.raw "⇄ switch · ■ stop · X stop others"
          match standbyLabel.Length > 0 with
          | true ->
            let color =
              match standbyLabel with
              | s when s.Contains "✓" -> "var(--green)"
              | s when s.Contains "⏳" -> "var(--fg-yellow)"
              | s when s.Contains "⚠" -> "var(--red)"
              | _ -> "var(--fg-dim)"
            Elem.span
              [ Attr.style (sprintf " · font-size: 0.65rem; color: %s;" color) ]
              [ Text.raw (sprintf " · %s" standbyLabel) ]
          | false -> ()
        ]
        match sessions.Length > 1 with
        | true ->
          Elem.button
            [ Attr.class' "session-btn session-btn-danger"
              Attr.style "font-size: 0.65rem; padding: 1px 6px;"
              Ds.onClick (Ds.post "/dashboard/session/stop-others") ]
            [ Text.raw "■ stop others" ]
        | false -> ()
      ]
  ]

let parseOutputLines (content: string) : OutputLine list =
  let tsKindRegex = Regex(@"^\[(\d{2}:\d{2}:\d{2})\]\s*\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  let kindOnlyRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = tsKindRegex.Match(l)
    match m.Success with
    | true ->
      { Timestamp = Some m.Groups.[1].Value
        Kind = OutputLineKind.fromString m.Groups.[2].Value
        Text = m.Groups.[3].Value }
    | false ->
      let m2 = kindOnlyRegex.Match(l)
      match m2.Success with
      | true ->
        { Timestamp = None
          Kind = OutputLineKind.fromString m2.Groups.[1].Value
          Text = m2.Groups.[2].Value }
      | false ->
        { Timestamp = None; Kind = ResultLine; Text = l })
  |> Array.toList

let parseDiagLines (content: string) : Diagnostic list =
  let diagRegex = Regex(@"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = diagRegex.Match(l)
    match m.Success with
    | true ->
      { Severity = DiagSeverity.fromString m.Groups.[1].Value
        Message = m.Groups.[4].Value
        Line = int m.Groups.[2].Value
        Col = int m.Groups.[3].Value }
    | false ->
      { Severity = match l.Contains("[error]") with | true -> DiagError | false -> DiagWarning
        Message = l
        Line = 0
        Col = 0 })
  |> Array.toList



/// Override parsed session statuses with live SessionState data.
/// The TUI text may be stale — live state is the source of truth.
let overrideSessionStatuses
  (getState: string -> SessionState)
  (getStatusMsg: string -> string option)
  (sessions: ParsedSession list) : ParsedSession list =
  sessions
  |> List.map (fun (s: ParsedSession) ->
    let liveStatus =
      match getState s.Id with
      | SessionState.Ready -> "running"
      | SessionState.Evaluating -> "running"
      | SessionState.WarmingUp -> "starting"
      | SessionState.Faulted -> "faulted"
      | SessionState.Uninitialized -> "stopped"
    { s with Status = liveStatus; StatusMessage = getStatusMsg s.Id })

/// Complete snapshot of all dashboard state needed for a single full-page render.
/// Constructed once per push, then passed to renderMainContent for atomic morph.
type DashboardSnapshot = {
  Version: string
  SessionState: string
  SessionId: string
  WorkingDir: string
  WarmupProgress: string
  EvalStats: EvalStatsView
  ThemeName: string
  ConnectionLabel: string option
  HotReloadPanel: XmlNode
  SessionContextPanel: XmlNode
  TestTracePanel: XmlNode
  OutputPanel: XmlNode
  SessionsPanel: XmlNode
  SessionPicker: XmlNode
  ThemePicker: XmlNode
  ThemeVars: XmlNode
}

/// Render the full dynamic content of the dashboard as a single <div id="main">.
/// This is the ONLY thing pushed via SSE on every state change.
/// Implements "immediate mode HTML" — the server renders the complete page from
/// state, sends one morph, and Datastar diffs the DOM.
/// See: "The Tao of Datastar" — https://data-star.dev/essays/tao_of_datastar
let renderMainContent (snap: DashboardSnapshot) : XmlNode =
  let connectionNode =
    match snap.ConnectionLabel with
    | Some label ->
      Elem.div [ Attr.id "connection-counts"; Attr.class' "meta"; Attr.style "font-size: 0.75rem; margin-top: 4px;" ] [
        Text.raw label
      ]
    | None ->
      Elem.div [ Attr.id "connection-counts"; Attr.class' "meta"; Attr.style "font-size: 0.75rem; margin-top: 4px;" ] []
  Elem.div [ Attr.id "main" ] [
    // Theme CSS variables — morphed with every push so theme changes propagate
    snap.ThemeVars
    // App header — version, status, stats, sidebar toggle
    Elem.div [ Attr.class' "app-header" ] [
      Elem.h1 [] [ Text.raw (sprintf "🧙 SageFs v%s" snap.Version) ]
      Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.75rem;" ] [
        renderSessionStatus snap.SessionState snap.SessionId snap.WorkingDir snap.WarmupProgress
        renderEvalStats snap.EvalStats
        Elem.button
          [ Attr.class' "sidebar-toggle"
            Ds.onEvent ("click", "$sidebarOpen = !$sidebarOpen")
            Ds.text "$sidebarOpen ? '✕ Panel' : '☰ Panel'" ]
          []
      ]
    ]
    // Main app layout: output+eval on left, sidebar on right
    Elem.div [ Attr.class' "app-layout" ] [
      Elem.div [ Attr.class' "main-area" ] [
        // Session picker — shown when no sessions exist, hidden otherwise
        snap.SessionPicker
        Elem.div [ Attr.id "editor-area" ] [
          Elem.div [ Attr.id "output-section"; Attr.class' "output-area" ] [
            Elem.div [ Attr.class' "output-header" ] [
              Elem.h2 [] [ Text.raw "Output" ]
              Elem.button
                [ Attr.class' "panel-header-btn"
                  Ds.onClick (Ds.post "/dashboard/clear-output") ]
                [ Text.raw "Clear" ]
            ]
            snap.OutputPanel
          ]
          // Eval area — collapsed by default via <details>
          Elem.create "details" [ Attr.id "evaluate-section"; Attr.class' "eval-area" ] [
            Elem.create "summary" [ Attr.style "cursor: pointer; display: flex; align-items: center; justify-content: space-between;" ] [
              Elem.span [ Attr.style "color: var(--accent); font-weight: bold; font-size: 0.9rem;" ] [ Text.raw "▸ Evaluate" ]
              Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5rem;" ] [
                Elem.span [ Attr.class' "meta"; Attr.style "font-size: 0.75rem;" ] [
                  Elem.span [ Ds.text """$code ? ($code.split('\\n').length + 'L ' + $code.length + 'c') : ''""" ] []
                ]
                Elem.button
                  [ Attr.class' "panel-header-btn"
                    Ds.onEvent ("click", "event.stopPropagation(); $helpVisible = !$helpVisible") ]
                  [ Text.raw "⌨" ]
              ]
            ]
            Elem.div [ Attr.id "keyboard-help-wrapper"; Ds.show "$helpVisible" ] [
              renderKeyboardHelp ()
            ]
            Elem.input [ Attr.type' "hidden"; Ds.bind "sessionId" ]
            Elem.div [ Attr.style "position: relative;" ] [
              Elem.textarea
                [ Attr.class' "eval-input"
                  Attr.id "eval-textarea"
                  Ds.bind "code"
                  Attr.create "placeholder" "Enter F# code... (Alt+Enter to eval, ;; auto-appended)"
                  Ds.onEvent ("keydown", "if(event.altKey && event.key === 'Enter') { event.preventDefault(); @post('/dashboard/eval') } if(event.ctrlKey && event.key === 'l') { event.preventDefault(); @post('/dashboard/clear-output') } if(event.key === 'Tab') { event.preventDefault(); var s=this.selectionStart; var e=this.selectionEnd; this.value=this.value.substring(0,s)+'  '+this.value.substring(e); this.selectionStart=this.selectionEnd=s+2; this.dispatchEvent(new Event('input')) } if(event.key === 'Escape') { document.getElementById('completion-dropdown').style.display='none' }")
                  Ds.onEvent ("input", "clearTimeout(window._compTimer); var ta=this; window._compTimer=setTimeout(function(){ var code=ta.value; var pos=ta.selectionStart; if(pos>0 && (code[pos-1]==='.' || (code[pos-1]>='a' && code[pos-1]<='z') || (code[pos-1]>='A' && code[pos-1]<='Z'))) { var sid=document.querySelector('[data-signal-sessionId]'); var sidVal=sid?sid.value:''; fetch('/dashboard/completions',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code:code,cursorPos:pos,sessionId:sidVal})}).then(r=>r.json()).then(d=>{ var dd=document.getElementById('completion-dropdown'); if(d.completions && d.completions.length>0){ dd.innerHTML=d.completions.map(function(c,i){return '<div class=\"comp-item\" data-insert=\"'+c.insertText+'\" style=\"padding:2px 6px;cursor:pointer;\"'+(i===0?' style=\"background:var(--selection)\"':'')+'>'+c.label+' <span style=\"opacity:0.5;font-size:0.8em\">('+c.kind+')</span></div>'}).join(''); dd.style.display='block'; dd.querySelectorAll('.comp-item').forEach(function(el){ el.onclick=function(){ var ins=el.dataset.insert; var before=ta.value.substring(0,pos); var wordStart=before.search(/[a-zA-Z0-9_]*$/); ta.value=ta.value.substring(0,wordStart)+ins+ta.value.substring(pos); ta.selectionStart=ta.selectionEnd=wordStart+ins.length; ta.dispatchEvent(new Event('input')); dd.style.display='none'; ta.focus(); }}) } else { dd.style.display='none' } }).catch(function(){}) } }, 300)")
                  Attr.create "spellcheck" "false" ]
                []
              Elem.div
                [ Attr.id "completion-dropdown"
                  Attr.style "display:none; position:absolute; bottom:100%; left:0; max-height:200px; overflow-y:auto; background:var(--bg); border:1px solid var(--selection); border-radius:4px; z-index:100; min-width:200px; font-size:0.85em; box-shadow:0 -2px 8px rgba(0,0,0,0.3);" ]
                []
            ]
            Elem.div [ Attr.style "display: flex; gap: 0.5rem; margin-top: 0.5rem; align-items: center;" ] [
              Elem.button
                [ Attr.class' "eval-btn"
                  Ds.indicator "evalLoading"
                  Ds.attr' ("disabled", "$evalLoading")
                  Ds.onClick (Ds.post "/dashboard/eval") ]
                [ Elem.span [ Ds.show "$evalLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$evalLoading" ] [ Text.raw "▶ " ]
                  Text.raw "Eval" ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "background: var(--green);"
                  Ds.onClick (Ds.post "/dashboard/reset") ]
                [ Text.raw "↻ Reset" ]
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "background: var(--red);"
                  Ds.onClick (Ds.post "/dashboard/hard-reset") ]
                [ Text.raw "⟳ Hard Reset" ]
              Elem.label
                [ Attr.class' "eval-btn"
                  Attr.style "background: var(--accent); cursor: pointer; display: inline-flex; align-items: center;" ]
                [ Elem.input
                    [ Attr.type' "file"
                      Attr.accept ".fs,.fsx,.fsi"
                      Attr.style "display: none;"
                      Attr.create "onchange" "if(this.files[0]){var f=this.files[0];var r=new FileReader();r.onload=function(){var ta=document.getElementById('eval-textarea');ta.value=r.result;ta.dispatchEvent(new Event('input'))};r.readAsText(f);this.value=''}" ]
                  Text.raw "📂 Load File" ]
            ]
            Elem.div [ Attr.id "eval-result" ] []
          ]
        ]
      ]
      // Resize handle between main area and sidebar
      Elem.div [ Attr.class' "resize-handle"; Attr.id "sidebar-resize" ] []
      // Sidebar — sessions, diagnostics, panels
      Elem.div [ Attr.id "sidebar"; Attr.class' "sidebar"; Ds.class' ("collapsed", "!$sidebarOpen") ] [
        Elem.div [ Attr.class' "sidebar-inner" ] [
          // Sessions panel
          Elem.div [ Attr.class' "panel" ] [
            Elem.h2 [] [ Text.raw "Sessions" ]
            connectionNode
            snap.SessionsPanel
          ]
          // Create Session
          Elem.div [ Attr.class' "panel" ] [
            Elem.h2 [] [ Text.raw "New Session" ]
            Elem.div [] [
              Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px;" ] [
                Text.raw "Working Directory"
              ]
              Elem.input
                [ Attr.class' "eval-input"
                  Attr.style "min-height: auto; height: 2rem;"
                  Ds.bind "newSessionDir"
                  Attr.create "placeholder" @"C:\path\to\project" ]
            ]
            Elem.div [ Attr.style "display: flex; gap: 4px; margin-top: 0.5rem;" ] [
              Elem.button
                [ Attr.class' "eval-btn"
                  Attr.style "flex: 1; height: 2rem; padding: 0 0.5rem; font-size: 0.8rem;"
                  Ds.indicator "discoverLoading"
                  Ds.attr' ("disabled", "$discoverLoading")
                  Ds.onClick (Ds.post "/dashboard/discover-projects") ]
                [ Elem.span [ Ds.show "$discoverLoading" ] [ Text.raw "⏳ " ]
                  Elem.span [ Ds.show "!$discoverLoading" ] [ Text.raw "🔍 " ]
                  Text.raw "Discover" ]
            ]
            Elem.div [ Attr.id "discovered-projects" ] []
            Elem.div [ Attr.style "margin-top: 0.5rem;" ] [
              Elem.label [ Attr.class' "meta"; Attr.style "display: block; margin-bottom: 4px;" ] [
                Text.raw "Projects (comma-sep)"
              ]
              Elem.input
                [ Attr.class' "eval-input"
                  Attr.style "min-height: auto; height: 2rem;"
                  Ds.bind "manualProjects"
                  Attr.create "placeholder" "MyProject.fsproj" ]
            ]
            Elem.button
              [ Attr.class' "eval-btn"
                Attr.style "margin-top: 0.5rem; width: 100%; font-size: 0.8rem;"
                Ds.indicator "createLoading"
                Ds.attr' ("disabled", "$createLoading")
                Ds.onClick (Ds.post "/dashboard/session/create") ]
              [ Elem.span [ Ds.show "$createLoading" ] [ Text.raw "⏳ Creating... " ]
                Elem.span [ Ds.show "!$createLoading" ] [ Text.raw "➕ Create" ] ]
          ]
          // Dynamic sidebar panels — rendered from current state
          snap.TestTracePanel
          snap.HotReloadPanel
          snap.SessionContextPanel
          // Theme picker
          Elem.div [ Attr.class' "panel" ] [
            Elem.h2 [] [ Text.raw "Theme" ]
            snap.ThemePicker
          ]
        ]
      ]
    ]
  ]

let renderRegionForSse (getSessionState: string -> SessionState) (getStatusMsg: string -> string option) (standbyLabel: string) (region: RenderRegion) =
  match region.Id with
  | "output" -> Some (renderOutput (parseOutputLines region.Content))
  | "sessions" ->
    let parsed = parseSessionLines region.Content
    let corrected = overrideSessionStatuses getSessionState getStatusMsg parsed
    let visible = corrected |> List.filter (fun s -> s.Status <> "stopped")
    Some (renderSessions visible (isCreatingSession region.Content) standbyLabel)
  | _ -> None

let pushRegions
  (ctx: HttpContext)
  (regions: RenderRegion list)
  (getPreviousSessions: unit -> Threading.Tasks.Task<PreviousSession list>)
  (getSessionState: string -> SessionState)
  (getStatusMsg: string -> string option)
  (standbyLabel: string)
  = task {
    for region in regions do
      match renderRegionForSse getSessionState getStatusMsg standbyLabel region with
      | Some html -> do! ssePatchNode ctx html
      | None -> ()
      // When sessions region is pushed, also push picker visibility
      match region.Id = "sessions" with
      | true ->
        let sessions = parseSessionLines region.Content
        let creating = isCreatingSession region.Content
        match sessions.IsEmpty && not creating with
        | true ->
          let! previous = getPreviousSessions ()
          do! ssePatchNode ctx (renderSessionPicker previous)
        | false ->
          do! ssePatchNode ctx renderSessionPickerEmpty
      | false -> ()
  }

/// Decides whether a theme push is needed after a state change.
/// Returns Some themeName if push needed, None otherwise.
/// Pure function — no side effects — for testability.
let resolveThemePush
  (themes: System.Collections.Generic.IDictionary<string, string>)
  (currentSessionId: string)
  (currentWorkingDir: string)
  (previousSessionId: string)
  (previousWorkingDir: string)
  : string option =
  let sessionChanged =
    currentSessionId.Length > 0 && currentSessionId <> previousSessionId
  let workingDirChanged =
    currentWorkingDir.Length > 0 && currentWorkingDir <> previousWorkingDir
  match sessionChanged || workingDirChanged with
  | true ->
    match currentWorkingDir.Length > 0 with
    | true ->
      match themes.TryGetValue(currentWorkingDir) with
      | true, n -> Some n
      | false, _ -> Some defaultThemeName
    | false ->
      Some defaultThemeName
  | false ->
    None

/// Render the hot-reload panel with a file list grouped by directory.
let renderHotReloadPanel (sessionId: string) (files: {| path: string; watched: bool |} list) (watchedCount: int) =
  let total = List.length files
  let grouped =
    files
    |> List.groupBy (fun f ->
      let normalized = f.path.Replace('\\', '/')
      match normalized.LastIndexOf('/') with
      | -1 -> ""
      | idx -> normalized.[..idx])
    |> List.sortBy fst
  Elem.div [ Attr.id "hot-reload-panel"; Attr.class' "panel" ] [
    Elem.h2 [] [ Text.raw "Hot Reload" ]
    Elem.div [ Attr.class' "meta"; Attr.style "margin-bottom: 0.5rem; font-size: 0.8rem;" ] [
      Text.raw (sprintf "%d of %d files watched" watchedCount total)
    ]
    Elem.div [ Attr.style "display: flex; gap: 4px; margin-bottom: 0.5rem;" ] [
      Elem.button
        [ Attr.class' "eval-btn"
          Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;"
          Attr.create "onclick" (sprintf "fetch('/api/sessions/%s/hotreload/watch-all',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})" sessionId) ]
        [ Text.raw "Watch All" ]
      Elem.button
        [ Attr.class' "eval-btn"
          Attr.style "flex: 1; height: 1.5rem; padding: 0 0.5rem; font-size: 0.7rem;"
          Attr.create "onclick" (sprintf "fetch('/api/sessions/%s/hotreload/unwatch-all',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})" sessionId) ]
        [ Text.raw "Unwatch All" ]
    ]
    Elem.div [ Attr.style "max-height: 200px; overflow-y: auto; font-size: 0.75rem;" ] [
      yield! grouped |> List.collect (fun (dir, dirFiles) ->
        let dirLabel =
          match dir.Length > 40 with
          | true -> "..." + dir.[dir.Length - 37..]
          | false -> dir
        let dirWatchedCount = dirFiles |> List.filter (fun f -> f.watched) |> List.length
        let allWatched = dirWatchedCount = List.length dirFiles
        let dirIcon = match allWatched, dirWatchedCount > 0 with | true, _ -> "●" | false, true -> "◐" | false, false -> "○"
        let dirColor = match allWatched || dirWatchedCount > 0 with | true -> "var(--accent, #7aa2f7)" | false -> "var(--fg-dim, #565f89)"
        let dirAction = match allWatched with | true -> "unwatch-directory" | false -> "watch-directory"
        let dirKey = "directory"
        [
          Elem.div
            [ Attr.style "font-weight: 600; margin-top: 4px; opacity: 0.8; font-size: 0.7rem; cursor: pointer; display: flex; align-items: center; gap: 4px;"
              Attr.create "onclick" (sprintf "fetch('/api/sessions/%s/hotreload/%s',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({%s:'%s'})})" sessionId dirAction dirKey (dir.Replace("\\", "\\\\"))) ]
            [ Elem.span [ Attr.style (sprintf "color: %s;" dirColor) ] [ Text.raw dirIcon ]
              Text.raw (sprintf "📁 %s (%d/%d)" dirLabel dirWatchedCount (List.length dirFiles)) ]
          yield! dirFiles |> List.map (fun f ->
            let fileName =
              let n = f.path.Replace('\\', '/')
              match n.LastIndexOf('/') with
              | -1 -> n
              | idx -> n.[idx + 1..]
            let icon = match f.watched with | true -> "●" | false -> "○"
            let color = match f.watched with | true -> "var(--accent, #7aa2f7)" | false -> "var(--fg-dim, #565f89)"
            Elem.div
              [ Attr.style "cursor: pointer; padding: 1px 4px; display: flex; align-items: center; gap: 4px;"
                Attr.create "onclick" (sprintf "fetch('/api/sessions/%s/hotreload/toggle',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({path:'%s'})})" sessionId (f.path.Replace("\\", "\\\\"))) ]
              [ Elem.span [ Attr.style (sprintf "color: %s; font-size: 0.8rem;" color) ] [ Text.raw icon ]
                Elem.span [ Attr.style (match f.watched with | true -> "opacity: 1" | false -> "opacity: 0.6") ] [ Text.raw fileName ] ]
          )
        ])
    ]
  ]

/// Render empty hot-reload panel when no session is active.
let renderHotReloadEmpty =
  Elem.div [ Attr.id "hot-reload-panel"; Attr.class' "panel" ] [
    Elem.h2 [] [ Text.raw "Hot Reload" ]
    Elem.div [ Attr.class' "meta"; Attr.style "font-size: 0.8rem;" ] [
      Text.raw "No active session"
    ]
  ]

/// Render session context panel with warmup details (assemblies, namespaces, files).
/// Uses HTML <details>/<summary> so it's collapsed by default.
let renderSessionContextPanel (ctx: SessionContext) =
  let summaryText = SessionContext.summary ctx

  let assembliesSection =
    Elem.details [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        Text.raw (sprintf "📦 Assemblies (%d)" (ctx.Warmup.AssembliesLoaded |> List.length))
      ]
      Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.2em; font-size: 0.7rem;" ] [
        for asm in ctx.Warmup.AssembliesLoaded do
          Elem.li [] [ Text.raw (SessionContext.assemblyLine asm) ]
      ]
    ]

  let namespacesSection =
    let opened = ctx.Warmup.NamespacesOpened
    let failed = ctx.Warmup.FailedOpens
    Elem.details [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        Text.raw (sprintf "📂 Namespaces (%d opened, %d failed)"
          (opened |> List.length) (failed |> List.length))
      ]
      Elem.div [ Attr.style "font-size: 0.7rem;" ] [
        Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.2em;" ] [
          for b in opened do
            Elem.li [] [
              Elem.code [] [ Text.raw (SessionContext.openLine b) ]
              match b.DurationMs > 0.0 with
              | true ->
                Elem.span [ Attr.style "color: #888; margin-left: 0.5em;" ] [
                  Text.raw (sprintf "(%.1fms)" b.DurationMs)
                ]
              | false -> ()
            ]
        ]
        match List.isEmpty failed with
        | false ->
          Elem.details [ Attr.create "open" ""; Attr.style "margin-top: 0.5em;" ] [
            Elem.summary [ Attr.style "color: #e74c3c; cursor: pointer; font-weight: bold;" ] [
              Text.raw (sprintf "⚠️ %d Failed Opens (expanded)" failed.Length)
            ]
            Elem.div [ Attr.style "padding-left: 0.5em;" ] [
              for f in failed do
                Elem.div [ Attr.style "margin: 0.4em 0; padding: 0.4em; background: rgba(231,76,60,0.08); border-left: 3px solid #e74c3c; border-radius: 3px;" ] [
                  Elem.div [ Attr.style "font-weight: bold; color: #e74c3c;" ] [
                    let kind = match f.IsModule with | true -> "module" | false -> "namespace"
                    Text.raw (sprintf "✖ %s (%s)" f.Name kind)
                    match f.RetryCount > 1 with
                    | true ->
                      Elem.span [ Attr.style "color: #888; font-weight: normal; margin-left: 0.5em;" ] [
                        Text.raw (sprintf "(%d retries)" f.RetryCount)
                      ]
                    | false -> ()
                  ]
                  Elem.div [ Attr.style "color: #c0392b; margin-top: 0.2em;" ] [
                    Text.raw f.ErrorMessage
                  ]
                  match List.isEmpty f.Diagnostics with
                  | false ->
                    Elem.ul [ Attr.style "margin: 0.2em 0; padding-left: 1.2em; list-style: none;" ] [
                      for d in f.Diagnostics do
                        let sevColor =
                          match d.Severity with
                          | "error" -> "#e74c3c"
                          | "warning" -> "#f39c12"
                          | _ -> "#3498db"
                        Elem.li [ Attr.style (sprintf "color: %s; margin: 0.15em 0;" sevColor) ] [
                          Elem.code [ Attr.style "background: rgba(0,0,0,0.1); padding: 0.1em 0.3em; border-radius: 2px; font-size: 0.65rem;" ] [
                            Text.raw (sprintf "FS%04d" d.ErrorNumber)
                          ]
                          match d.FileName with
                          | Some fn ->
                            Elem.span [ Attr.style "margin-left: 0.4em; color: #888;" ] [
                              Text.raw (sprintf "%s:%d:%d" fn d.StartLine d.StartColumn)
                            ]
                          | None -> ()
                          Elem.span [ Attr.style "margin-left: 0.4em;" ] [
                            Text.raw d.Message
                          ]
                        ]
                    ]
                  | true -> ()
                ]
            ]
          ]
        | true -> ()
      ]
    ]

  let timingSection =
    let t = ctx.Warmup.PhaseTiming
    Elem.details [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        Text.raw (sprintf "⏱️ Warmup Timing (%dms total)" t.TotalMs)
      ]
      Elem.div [ Attr.style "font-size: 0.7rem; padding-left: 0.5em;" ] [
        let phases = [
          "Scan source files", t.ScanSourceFilesMs
          "Scan assemblies", t.ScanAssembliesMs
          "Open namespaces", t.OpenNamespacesMs
        ]
        let maxMs = match t.TotalMs with | 0L -> 1L | v -> v
        for (label, ms) in phases do
          let pct = float ms / float maxMs * 100.0
          Elem.div [ Attr.style "margin: 0.2em 0;" ] [
            Elem.div [ Attr.style "display: flex; align-items: center; gap: 0.5em;" ] [
              Elem.span [ Attr.style "min-width: 120px;" ] [ Text.raw label ]
              Elem.div [ Attr.style "flex: 1; height: 8px; background: rgba(0,0,0,0.1); border-radius: 4px; overflow: hidden;" ] [
                Elem.div [ Attr.style (sprintf "width: %.1f%%; height: 100%%; background: #3498db; border-radius: 4px;" pct) ] []
              ]
              Elem.span [ Attr.style "min-width: 50px; text-align: right; color: #888;" ] [
                Text.raw (sprintf "%dms" ms)
              ]
            ]
          ]
      ]
    ]

  let filesSection =
    Elem.details [] [
      Elem.summary [ Attr.style "font-size: 0.75rem; cursor: pointer;" ] [
        let loadedCount =
          ctx.FileStatuses
          |> List.filter (fun f -> f.Readiness = Loaded)
          |> List.length
        Text.raw (sprintf "📄 Files (%d/%d loaded)" loadedCount (ctx.FileStatuses |> List.length))
      ]
      Elem.ul [ Attr.style "margin: 2px 0; padding-left: 1.2em; font-size: 0.7rem;" ] [
        for f in ctx.FileStatuses do
          let color =
            match f.Readiness with
            | Loaded -> "#2ecc71"
            | Stale -> "#f39c12"
            | LoadFailed -> "#e74c3c"
            | NotLoaded -> "#95a5a6"
          Elem.li [ Attr.style (sprintf "color: %s" color) ] [
            Text.raw (SessionContext.fileLine f)
          ]
      ]
    ]

  Elem.div [ Attr.id "session-context"; Attr.class' "panel" ] [
    Elem.details [] [
      Elem.summary [ Attr.style "cursor: pointer; font-weight: bold; font-size: 0.8rem;" ] [
        Text.raw (sprintf "🔍 Session Context: %s" summaryText)
      ]
      Elem.div [ Attr.style "padding-left: 0.5em; margin-top: 0.3em;" ] [
        timingSection
        assembliesSection
        namespacesSection
        filesSection
      ]
    ]
  ]

/// Render empty session context panel when no session is active.
let renderSessionContextEmpty =
  Elem.div [ Attr.id "session-context"; Attr.class' "panel" ] [
    Elem.div [ Attr.style "font-size: 0.8rem; opacity: 0.6;" ] [
      Text.raw "No session context"
    ]
  ]

// ── Test Trace Panel ──────────────────────────────────────────────

let private renderTestPhase (label: string) (ms: float) (maxMs: float) (icon: string) =
  let pct = match maxMs > 0.0 with | true -> min 100.0 (ms / maxMs * 100.0) | false -> 0.0
  let color =
    match ms with
    | ms when ms < 50.0 -> "var(--green, #27ae60)"
    | ms when ms < 500.0 -> "var(--yellow, #f39c12)"
    | _ -> "var(--red, #e74c3c)"
  Elem.div [ Attr.style "margin-bottom: 4px;" ] [
    Elem.div [ Attr.style "display: flex; justify-content: space-between; font-size: 0.75rem;" ] [
      Elem.span [] [ Text.raw (sprintf "%s %s" icon label) ]
      Elem.span [] [ Text.raw (sprintf "%.0fms" ms) ]
    ]
    Elem.div [ Attr.style "height: 4px; background: var(--bg-alt, #2a2a2a); border-radius: 2px; overflow: hidden;" ] [
      Elem.div [ Attr.style (sprintf "width: %.0f%%; height: 100%%; background: %s; border-radius: 2px;" pct color) ] []
    ]
  ]

let renderTestTracePanel
  (timing: Features.LiveTesting.TestCycleTiming option)
  (isRunning: bool)
  (summary: Features.LiveTesting.TestSummary)
  =
  let statusLabel =
    match summary.Enabled, isRunning with
    | false, _ -> "⏸ disabled"
    | true, true -> "⏳ running"
    | true, false -> "✅ idle"
  let summaryParts = [
    yield Elem.span [ Attr.style "color: var(--green, #27ae60);" ] [
      Text.raw (sprintf "✓ %d" summary.Passed) ]
    yield Elem.span [ Attr.style "color: var(--red, #e74c3c);" ] [
      Text.raw (sprintf "✗ %d" summary.Failed) ]
    yield Elem.span [ Attr.style "opacity: 0.6;" ] [
      Text.raw (sprintf "/ %d" summary.Total) ]
    match summary.Stale > 0 with
    | true ->
      yield Elem.span [ Attr.style "color: var(--yellow, #f39c12);" ] [
        Text.raw (sprintf "⟳ %d stale" summary.Stale) ]
    | false -> ()
    match summary.Running > 0 with
    | true ->
      yield Elem.span [ Attr.style "color: var(--cyan, #3498db);" ] [
        Text.raw (sprintf "⏳ %d" summary.Running) ]
    | false -> ()
  ]
  let timingSection =
    match timing with
    | None ->
      Elem.div [ Attr.style "font-size: 0.75rem; opacity: 0.5; padding: 4px 0;" ] [
        Text.raw "No timing data yet" ]
    | Some t ->
      let tsMs, fcsMs, execMs =
        match t.Depth with
        | Features.LiveTesting.TestCycleDepth.TreeSitterOnly ts -> ts.TotalMilliseconds, 0.0, 0.0
        | Features.LiveTesting.TestCycleDepth.ThroughFcs (ts, fcs) -> ts.TotalMilliseconds, fcs.TotalMilliseconds, 0.0
        | Features.LiveTesting.TestCycleDepth.ThroughExecution (ts, fcs, exec) -> ts.TotalMilliseconds, fcs.TotalMilliseconds, exec.TotalMilliseconds
      let totalMs = tsMs + fcsMs + execMs
      let maxMs = max totalMs 1.0
      Elem.div [] [
        yield renderTestPhase "Tree-sitter" tsMs maxMs "🌳"
        match t.Depth with
        | Features.LiveTesting.TestCycleDepth.TreeSitterOnly _ -> ()
        | _ -> yield renderTestPhase "FCS Check" fcsMs maxMs "🔍"
        match t.Depth with
        | Features.LiveTesting.TestCycleDepth.ThroughExecution _ -> yield renderTestPhase "Execution" execMs maxMs "🧪"
        | _ -> ()
        yield Elem.div [ Attr.style "font-size: 0.7rem; opacity: 0.5; margin-top: 4px;" ] [
          Text.raw (sprintf "%.0fms total • %d/%d tests • %s"
            totalMs t.AffectedTests t.TotalTests
            (match t.Trigger with
             | Features.LiveTesting.RunTrigger.Keystroke -> "keystroke"
             | Features.LiveTesting.RunTrigger.FileSave -> "save"
             | Features.LiveTesting.RunTrigger.ExplicitRun -> "manual"))
        ]
      ]
  Elem.div [ Attr.id "test-trace"; Attr.class' "panel" ] [
    Elem.div [ Attr.style "display: flex; justify-content: space-between; align-items: center;" ] [
      Elem.h2 [ Attr.style "margin: 0;" ] [ Text.raw "Tests" ]
      Elem.span [ Attr.style "font-size: 0.7rem; opacity: 0.7;" ] [ Text.raw statusLabel ]
    ]
    Elem.div [ Attr.style "display: flex; gap: 8px; font-size: 0.75rem; margin: 6px 0;" ] summaryParts
    timingSection
  ]

let renderTestTraceEmpty =
  Elem.div [ Attr.id "test-trace"; Attr.class' "panel" ] [
    Elem.h2 [] [ Text.raw "Tests" ]
    Elem.div [ Attr.style "font-size: 0.8rem; opacity: 0.6;" ] [
      Text.raw "No active session"
    ]
  ]

/// Create the SSE stream handler that pushes Elm state to the browser.
let createStreamHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  : HttpHandler =
  fun ctx -> task {
    SageFs.Instrumentation.sseConnectionsActive.Add(1L)
    Response.sseStartResponse ctx |> ignore

    let clientId = Guid.NewGuid().ToString("N").[..7]
    // Resolve initial session: first available session (observer behavior — don't create)
    let! sessions = q.GetAllSessions ()
    let mutable currentSessionId =
      sessions |> List.tryHead |> Option.map (fun s -> s.Id) |> Option.defaultValue ""
    infra.ConnectionTracker |> Option.iter (fun t -> t.Register(clientId, Browser, currentSessionId))
    let mutable lastSessionId = ""
    let mutable lastWorkingDir = ""
    let mutable lastOutputHash = 0
    let mutable lastThemeName = defaultThemeName

    let pushState () = task {
      // === FULL-PAGE MORPH: "The Tao of Datastar" ===
      // Build a complete DashboardSnapshot from current state, render it all
      // into one <div id="main">, and push a single SSE morph. Datastar diffs.
      // See module-level doc comment for philosophy.

      // Track daemon's active session for theme switching
      let activeId = q.GetActiveSessionId ()
      match activeId.Length > 0 with
      | true -> currentSessionId <- activeId
      | false -> ()
      let state = q.GetSessionState currentSessionId
      let stateStr = SessionState.label state
      let workingDir = q.GetSessionWorkingDir currentSessionId
      // Parallelize all worker HTTP fetches — prevents sequential 2s+ timeouts
      let statsTask = q.GetEvalStats currentSessionId
      let hrTask = q.GetHotReloadState currentSessionId
      let wCtxTask = q.GetWarmupContext currentSessionId
      do! System.Threading.Tasks.Task.WhenAll(statsTask, hrTask, wCtxTask)
      let stats = statsTask.Result
      let hrState = hrTask.Result
      let wCtx = wCtxTask.Result
      // Push sessionId signal so eval form can include it
      do! Response.ssePatchSignal ctx (SignalPath.sp "sessionId") currentSessionId
      let avgMs =
        match stats.EvalCount > 0 with
        | true -> stats.TotalDuration.TotalMilliseconds / float stats.EvalCount
        | false -> 0.0
      // Resolve theme
      let themeName =
        match resolveThemePush infra.SessionThemes currentSessionId workingDir lastSessionId lastWorkingDir with
        | Some name -> lastThemeName <- name; name
        | None -> lastThemeName
      lastSessionId <- currentSessionId
      lastWorkingDir <- workingDir
      // Build connection label
      let connectionLabel =
        match infra.ConnectionTracker with
        | Some tracker ->
          let counts = tracker.GetAllCounts()
          let parts =
            [ match counts.Browsers > 0 with | true -> sprintf "🌐 %d" counts.Browsers | false -> ()
              match counts.McpAgents > 0 with | true -> sprintf "🤖 %d" counts.McpAgents | false -> ()
              match counts.Terminals > 0 with | true -> sprintf "💻 %d" counts.Terminals | false -> () ]
          match parts.IsEmpty with
          | true -> Some (sprintf "%d connected" tracker.TotalCount)
          | false -> Some (String.Join(" ", parts))
        | None -> None
      // Build hot-reload panel
      let hrPanel =
        match currentSessionId.Length > 0 with
        | true ->
          match hrState with
          | Some hr -> renderHotReloadPanel currentSessionId hr.files hr.watchedCount
          | None -> renderHotReloadEmpty
        | false -> renderHotReloadEmpty
      // Build session context panel
      let scPanel =
        match currentSessionId.Length > 0 with
        | true ->
          match wCtx with
          | Some ctx' ->
            let fileStatuses =
              match hrState with
              | Some hr ->
                hr.files |> List.map (fun f ->
                  let readiness =
                    ctx'.NamespacesOpened
                    |> List.exists (fun b -> f.path.EndsWith(b.Name, StringComparison.OrdinalIgnoreCase))
                    |> fun loaded -> match loaded with | true -> FileReadiness.Loaded | false -> FileReadiness.NotLoaded
                  { Path = f.path; Readiness = readiness; LastLoadedAt = None; IsWatched = f.watched })
              | None -> []
            renderSessionContextPanel
              { SessionId = currentSessionId
                ProjectNames = []
                WorkingDir = q.GetSessionWorkingDir currentSessionId
                Status = SessionState.label (q.GetSessionState currentSessionId)
                Warmup = ctx'
                FileStatuses = fileStatuses }
          | None -> renderSessionContextEmpty
        | false -> renderSessionContextEmpty
      // Build test trace panel
      let ttPanel =
        match q.GetTestTrace () with
        | Some trace -> renderTestTracePanel trace.Timing trace.IsRunning trace.Summary
        | None -> renderTestTraceEmpty
      // Build output + sessions from Elm regions
      let! outputPanel, sessionsPanel, sessionPicker = task {
        match q.GetElmRegions () with
        | Some regions ->
          let outputRegion = regions |> List.tryFind (fun r -> r.Id = "output")
          let outputHash = outputRegion |> Option.map (fun r -> r.Content.GetHashCode()) |> Option.defaultValue 0
          let outNode =
            match outputRegion with
            | Some r -> renderOutput (parseOutputLines r.Content)
            | None -> renderOutput []
          lastOutputHash <- outputHash
          let sessRegion = regions |> List.tryFind (fun r -> r.Id = "sessions")
          match sessRegion with
          | Some r ->
            let parsed = parseSessionLines r.Content
            let corrected = overrideSessionStatuses q.GetSessionState q.GetStatusMsg parsed
            let visible = corrected |> List.filter (fun s -> s.Status <> "stopped")
            let creating = isCreatingSession r.Content
            let! standby = q.GetStandbyInfo ()
            let sLabel = StandbyInfo.label standby
            let sess = renderSessions visible creating sLabel
            let! pick =
              match visible.IsEmpty && not creating with
              | true ->
                task {
                  let! previous = q.GetPreviousSessions ()
                  return renderSessionPicker previous
                }
              | false -> task { return renderSessionPickerEmpty }
            return (outNode, sess, pick)
          | None ->
            return (outNode, renderSessions [] false "", renderSessionPickerEmpty)
        | None ->
          return (renderOutput [], renderSessions [] false "", renderSessionPickerEmpty)
      }
      // Compose everything into one snapshot and push ONE morph
      let snap : DashboardSnapshot = {
        Version = infra.Version
        SessionState = stateStr
        SessionId = currentSessionId
        WorkingDir = workingDir
        WarmupProgress = q.GetWarmupProgress currentSessionId
        EvalStats =
          { Count = stats.EvalCount
            AvgMs = avgMs
            MinMs = stats.MinDuration.TotalMilliseconds
            MaxMs = stats.MaxDuration.TotalMilliseconds }
        ThemeName = themeName
        ConnectionLabel = connectionLabel
        HotReloadPanel = hrPanel
        SessionContextPanel = scPanel
        TestTracePanel = ttPanel
        OutputPanel = outputPanel
        SessionsPanel = sessionsPanel
        SessionPicker = sessionPicker
        ThemePicker = renderThemePicker themeName
        ThemeVars = renderThemeVars themeName
      }
      do! ssePatchNode ctx (renderMainContent snap)
    }

    try
      // Push initial state (catch all exceptions — don't let a transient failure kill the stream)
      try
        do! pushState ()
      with ex ->
        Log.error "[Dashboard SSE] Initial pushState failed: %s" ex.Message

      match infra.StateChanged with
      | Some evt ->
        let tcs = Threading.Tasks.TaskCompletionSource()
        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
        // Serialize SSE writes via MailboxProcessor — no locks, no mutable state.
        // Coalesces rapid state changes: drain queued, throttle 100ms, drain again, push once.
        // Heartbeat: when idle >15s, sends `: keepalive\n\n` SSE comment to prevent
        // proxy/browser timeouts. Integrated into the actor loop to avoid concurrent writes.
        let pushAgent = MailboxProcessor.Start((fun inbox ->
          let rec loop () = async {
            let! msg = inbox.TryReceive(15_000)
            match msg with
            | None ->
              // Idle timeout — send SSE keepalive comment
              try
                let bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n\n")
                do! ctx.Response.Body.AsyncWrite(bytes, 0, bytes.Length)
                do! ctx.Response.Body.FlushAsync() |> Async.AwaitTask
              with
              | :? System.IO.IOException -> ()
              | :? ObjectDisposedException -> ()
              | :? OperationCanceledException -> ()
              return! loop ()
            | Some () ->
              // Got a state change — drain + coalesce + push
              while inbox.CurrentQueueLength > 0 do
                do! inbox.Receive()
              do! Async.Sleep 100
              while inbox.CurrentQueueLength > 0 do
                do! inbox.Receive()
              try
                do! pushState () |> Async.AwaitTask
              with
              | :? System.IO.IOException -> ()
              | :? ObjectDisposedException -> ()
              | :? OperationCanceledException -> ()
              | ex -> Log.error "[Dashboard SSE] pushState failed: %s" ex.Message
              return! loop ()
          }
          loop ()), ctx.RequestAborted)
        use _sub = evt.Subscribe(fun _ ->
          try pushAgent.Post(())
          with :? ObjectDisposedException -> ())
        do! tcs.Task
      | None ->
        // Fallback: poll every second
        while not ctx.RequestAborted.IsCancellationRequested do
          try
            do! Threading.Tasks.Task.Delay(Timeouts.sseEventInterval, ctx.RequestAborted)
            do! pushState ()
          with
          | :? OperationCanceledException -> ()
    finally
      SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
      infra.ConnectionTracker |> Option.iter (fun t -> t.Unregister(clientId))
  }

/// Create the eval POST handler.
let createEvalHandler
  (evalCode: string -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      use! doc = Request.getSignalsJson ctx
      let code =
        match doc.RootElement.TryGetProperty("code") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let sessionId =
        match doc.RootElement.TryGetProperty("sessionId") with
        | true, prop -> prop.GetString()
        | _ -> ""
      match String.IsNullOrWhiteSpace code with
      | true ->
        Response.sseStartResponse ctx |> ignore
        do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
      | false ->
        let codeWithTerminator =
          let trimmed = code.TrimEnd()
          match trimmed.EndsWith(";;") with
          | true -> code
          | false -> sprintf "%s;;" trimmed
        let! result = evalCode sessionId codeWithTerminator
        Response.sseStartResponse ctx |> ignore
        do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
        let displayResult, cssClass =
          match result with
          | Ok msg -> msg, "output-line output-result"
          | Error err ->
            err
              .Replace("FSharp.Compiler.Interactive.Shell+FsiCompilationException: ", "")
              .Replace("Evaluation failed: ", "⚠ "),
            "output-line output-error"
        let resultHtml =
          Elem.div [ Attr.id "eval-result" ] [
            Elem.pre [ Attr.class' cssClass; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
              Text.raw displayResult
            ]
          ]
        do! ssePatchNode ctx resultHtml
    with
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the eval-file POST handler (reads file, evals its content).
let createEvalFileHandler
  (evalCode: string -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      use doc = System.Text.Json.JsonDocument.Parse(body)
      let filePath =
        match doc.RootElement.TryGetProperty("path") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let sessionId =
        match doc.RootElement.TryGetProperty("sessionId") with
        | true, prop -> prop.GetString()
        | _ -> ""
      match String.IsNullOrWhiteSpace filePath || not (File.Exists filePath) with
      | true ->
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = "File not found or path missing" |})
      | false ->
        let! code = File.ReadAllTextAsync(filePath)
        let codeWithTerminator =
          let trimmed = code.TrimEnd()
          match trimmed.EndsWith(";;") with
          | true -> code
          | false -> sprintf "%s;;" trimmed
        let! result = evalCode sessionId codeWithTerminator
        match result with
        | Ok msg -> do! ctx.Response.WriteAsJsonAsync({| success = true; result = msg |})
        | Error err ->
          ctx.Response.StatusCode <- 422
          do! ctx.Response.WriteAsJsonAsync({| success = false; error = err |})
    with ex ->
      ctx.Response.StatusCode <- 500
      do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
  }
let createCompletionsHandler
  (getCompletions: string -> string -> int -> Threading.Tasks.Task<Features.AutoCompletion.CompletionItem list>)
  : HttpHandler =
  fun ctx -> task {
    try
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      use doc = System.Text.Json.JsonDocument.Parse(body)
      let code =
        match doc.RootElement.TryGetProperty("code") with
        | true, prop -> prop.GetString()
        | _ -> ""
      let cursorPos =
        match doc.RootElement.TryGetProperty("cursorPos") with
        | true, prop -> prop.GetInt32()
        | _ -> -1
      let sessionId =
        match doc.RootElement.TryGetProperty("sessionId") with
        | true, prop -> prop.GetString()
        | _ -> ""
      match String.IsNullOrWhiteSpace code || cursorPos < 0 with
      | true ->
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsJsonAsync({| completions = [||]; count = 0 |})
      | false ->
        let! items = getCompletions sessionId code cursorPos
        let json = McpAdapter.formatCompletionsJson items
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(json)
    with ex ->
      ctx.Response.StatusCode <- 500
      do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
  }

/// Create the reset POST handler.
let createResetHandler
  (resetSession: string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx -> task {
    try
      let! sessionId = task {
        try
          use! doc = Request.getSignalsJson ctx
          match doc.RootElement.TryGetProperty("sessionId") with
          | true, prop -> return prop.GetString()
          | _ -> return ""
        with _ -> return ""
      }
      let! result = resetSession sessionId
      Response.sseStartResponse ctx |> ignore
      let msg =
        match result with
        | Ok m -> m
        | Error e -> sprintf "Failed: %s" e
      let resultHtml =
        Elem.div [ Attr.id "eval-result" ] [
          Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw (sprintf "Reset: %s" msg)
          ]
        ]
      do! ssePatchNode ctx resultHtml
      // Clear stale output after reset (Bug #5)
      let clearedOutput =
        Elem.div [ Attr.id "output-panel" ] [
          Elem.span [ Attr.class' "meta"; Attr.style "padding: 0.5rem;" ] [
            Text.raw (sprintf "Reset: %s" msg)
          ]
        ]
      do! ssePatchNode ctx clearedOutput
    with
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create the session action handler (switch/stop).
let createSessionActionHandler
  (action: string -> Threading.Tasks.Task<Result<string, string>>)
  : string -> HttpHandler =
  fun sessionId ctx -> task {
    try
      let! result = action sessionId
      Response.sseStartResponse ctx |> ignore
      // Push sessionId so eval form targets the new session
      do! Response.ssePatchSignal ctx (SignalPath.sp "sessionId") sessionId
      let msg, cssClass =
        match result with
        | Ok m -> m, "output-line output-info"
        | Error e -> e, "output-line output-error"
      let resultHtml =
        Elem.div [ Attr.id "eval-result" ] [
          Elem.pre [ Attr.class' cssClass; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw msg
          ]
        ]
      do! ssePatchNode ctx resultHtml
    with
    | :? System.IO.IOException -> ()
    | :? System.ObjectDisposedException -> ()
  }

/// Create clear-output handler.
let createClearOutputHandler : HttpHandler =
  fun ctx -> task {
    Response.sseStartResponse ctx |> ignore
    let emptyOutput = Elem.div [ Attr.id "output-panel" ] [
      Elem.span [ Attr.class' "meta"; Attr.style "padding: 0.5rem;" ] [ Text.raw "No output yet" ]
    ]
    do! ssePatchNode ctx emptyOutput
  }

/// Render discovered projects as an SSE fragment.
let renderDiscoveredProjects (discovered: DiscoveredProjects) =
  Elem.div [ Attr.id "discovered-projects"; Attr.style "margin-top: 0.5rem;" ] [
    match discovered.Solutions.IsEmpty && discovered.Projects.IsEmpty with
    | true ->
      Elem.div [ Attr.class' "output-line output-error" ] [
        Text.raw (sprintf "No .sln/.fsproj found in %s" discovered.WorkingDir)
      ]
    | false ->
      Elem.div [ Attr.class' "output-line output-result" ] [
        Text.raw (sprintf "Found in %s:" discovered.WorkingDir)
      ]
      match discovered.Solutions.IsEmpty with
      | false ->
        yield! discovered.Solutions |> List.map (fun s ->
          Elem.div [ Attr.class' "output-line output-info"; Attr.style "padding-left: 1rem;" ] [
            Text.raw (sprintf "📁 %s (solution)" s)
          ])
      | true -> ()
      yield! discovered.Projects |> List.map (fun p ->
        Elem.div [ Attr.class' "output-line"; Attr.style "padding-left: 1rem;" ] [
          Text.raw (sprintf "📄 %s" p)
        ])
      Elem.div [ Attr.class' "meta"; Attr.style "margin-top: 4px;" ] [
        match discovered.Solutions.IsEmpty with
        | false ->
          Text.raw "Will use solution file. Click 'Create Session' to proceed."
        | true ->
          Text.raw "Will load all projects. Click 'Create Session' to proceed."
      ]
  ]

/// Helper: render an eval-result error fragment.
let evalResultError (msg: string) =
  Elem.div [ Attr.id "eval-result" ] [
    Elem.pre [ Attr.class' "output-line output-error"; Attr.style "margin-top: 0.5rem;" ] [
      Text.raw msg
    ]
  ]

/// Helper: resolve which projects to use from signal data.
/// Priority: manual > .SageFs/config.fsx > auto-discovery
let resolveSessionProjects (dir: string) (manualProjects: string) =
  let autoDetectProjects dir =
    let discovered = discoverProjects dir
    match discovered.Solutions.IsEmpty with
    | false -> [ Path.Combine(dir, discovered.Solutions.Head) ]
    | true ->
      match discovered.Projects.IsEmpty with
      | false -> discovered.Projects |> List.map (fun p -> Path.Combine(dir, p))
      | true -> []
  match String.IsNullOrWhiteSpace manualProjects with
  | false ->
    manualProjects.Split(',')
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.map (fun p ->
      match Path.IsPathRooted p with
      | true -> p
      | false -> Path.Combine(dir, p))
    |> Array.toList
  | true ->
    match DirectoryConfig.load dir with
    | Some config ->
      match config.Load with
      | Solution path ->
        let full = match Path.IsPathRooted path with | true -> path | false -> Path.Combine(dir, path)
        [ full ]
      | Projects paths ->
        paths |> List.map (fun p ->
          match Path.IsPathRooted p with
          | true -> p
          | false -> Path.Combine(dir, p))
      | NoLoad -> []
      | AutoDetect -> autoDetectProjects dir
    | _ -> autoDetectProjects dir

/// Helper: extract a signal by camelCase or kebab-case name.
let getSignalString (doc: System.Text.Json.JsonDocument) (camelCase: string) (kebab: string) =
  match doc.RootElement.TryGetProperty(camelCase) with
  | true, prop -> prop.GetString()
  | _ ->
    match doc.RootElement.TryGetProperty(kebab) with
    | true, prop -> prop.GetString()
    | _ -> ""

/// Push discover results for a directory.
let pushDiscoverResults (ctx: HttpContext) (dir: string) = task {
  let dirConfig = DirectoryConfig.load dir
  let discovered = discoverProjects dir
  let configNote =
    match dirConfig with
    | Some config ->
      match config.Load with
      | Solution path ->
        Some (Elem.div [ Attr.class' "output-line output-info"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw (sprintf "⚙️ .SageFs/config.fsx: solution %s" path)
        ])
      | Projects paths ->
        Some (Elem.div [ Attr.class' "output-line output-info"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw (sprintf "⚙️ .SageFs/config.fsx: %s" (String.Join(", ", paths)))
        ])
      | NoLoad ->
        Some (Elem.div [ Attr.class' "output-line meta"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw "⚙️ .SageFs/config.fsx: no project loading (bare session)"
        ])
      | AutoDetect ->
        Some (Elem.div [ Attr.class' "output-line meta"; Attr.style "margin-bottom: 4px;" ] [
          Text.raw "⚙️ .SageFs/config.fsx found (auto-detect projects)"
        ])
    | None -> None
  let mainContent = renderDiscoveredProjects discovered
  match configNote with
  | Some note ->
    let combined = Elem.div [ Attr.id "discovered-projects"; Attr.style "margin-top: 0.5rem;" ] [
      note; mainContent
    ]
    do! ssePatchNode ctx combined
  | None ->
    do! ssePatchNode ctx mainContent
}

/// Create the discover-projects POST handler.
let createDiscoverHandler : HttpHandler =
  fun ctx -> task {
    use! doc = Request.getSignalsJson ctx
    let dir = getSignalString doc "newSessionDir" "new-session-dir"
    Response.sseStartResponse ctx |> ignore
    match String.IsNullOrWhiteSpace dir, Directory.Exists dir with
    | true, _ ->
      do! ssePatchNode ctx (
        Elem.div [ Attr.id "discovered-projects" ] [
          Elem.span [ Attr.class' "output-line output-error" ] [
            Text.raw "Enter a working directory first"
          ]])
    | false, false ->
      do! ssePatchNode ctx (
        Elem.div [ Attr.id "discovered-projects" ] [
          Elem.span [ Attr.class' "output-line output-error" ] [
            Text.raw (sprintf "Directory not found: %s" dir)
          ]])
    | false, true ->
      do! pushDiscoverResults ctx dir
  }

/// Create the create-session POST handler.
let createCreateSessionHandler
  (createSession: string list -> string -> Threading.Tasks.Task<Result<string, string>>)
  (switchSession: (string -> Threading.Tasks.Task<Result<string, string>>) option)
  : HttpHandler =
  fun ctx -> task {
    use! doc = Request.getSignalsJson ctx
    let dir = getSignalString doc "newSessionDir" "new-session-dir"
    let manualProjects = getSignalString doc "manualProjects" "manual-projects"
    Response.sseStartResponse ctx |> ignore
    match String.IsNullOrWhiteSpace dir, Directory.Exists dir with
    | true, _ ->
      do! ssePatchNode ctx (evalResultError "Working directory is required")
    | false, false ->
      do! ssePatchNode ctx (evalResultError (sprintf "Directory not found: %s" dir))
    | false, true ->
      let projects = resolveSessionProjects dir manualProjects
      match projects.IsEmpty with
      | true ->
        do! ssePatchNode ctx (evalResultError "No projects found. Enter paths manually or check the directory.")
      | false ->
        let! result = createSession projects dir
        match result with
        | Ok newSessionId ->
          // Switch to the new session so the SSE stream picks it up
          match switchSession with
          | Some switch -> let! _ = switch newSessionId in ()
          | None -> ()
          // Push the new session's ID so the eval form targets it
          do! Response.ssePatchSignal ctx (SignalPath.sp "sessionId") newSessionId
          do! ssePatchNode ctx (
            Elem.div [ Attr.id "eval-result" ] [
              Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem;" ] [
                Text.raw (sprintf "Session '%s' created. Switched to it." newSessionId)
              ]
            ])
        | Error msg ->
          do! ssePatchNode ctx (evalResultError (sprintf "Failed: %s" msg))
        do! ssePatchNode ctx (Elem.div [ Attr.id "discovered-projects" ] [])
  }

/// JSON SSE stream for TUI clients — pushes regions + model summary as JSON.
let createApiStateHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  : HttpHandler =
  fun ctx -> task {
    SageFs.Instrumentation.sseConnectionsActive.Add(1L)
    ctx.Response.ContentType <- "text/event-stream"
    ctx.Response.Headers.["Cache-Control"] <- Microsoft.Extensions.Primitives.StringValues "no-cache"
    ctx.Response.Headers.["Connection"] <- Microsoft.Extensions.Primitives.StringValues "keep-alive"

    // Each SSE connection tracks its own session via query param
    let! sessions = q.GetAllSessions ()
    let defaultSid = sessions |> List.tryHead |> Option.map (fun s -> s.Id) |> Option.defaultValue ""
    let connSessionId =
      match ctx.Request.Query.TryGetValue("sessionId") with
      | true, v when v.Count > 0 && not (String.IsNullOrEmpty(v.[0])) -> v.[0]
      | _ -> defaultSid
    let clientId = sprintf "tui-%s" (Guid.NewGuid().ToString("N").[..7])
    infra.ConnectionTracker |> Option.iter (fun t -> t.Register(clientId, Terminal, connSessionId))

    let pushJson () = task {
      let activeSid = q.GetActiveSessionId ()
      let activeDir = q.GetSessionWorkingDir activeSid
      let state = q.GetSessionState activeSid
      let! stats = q.GetEvalStats activeSid
      let regions =
        match q.GetElmRegions () with
        | Some r ->
          r |> List.map (fun region ->
            {| id = region.Id
               content = region.Content
               cursor = region.Cursor |> Option.map (fun c -> {| line = c.Line; col = c.Col |})
               completions = region.Completions |> Option.map (fun co ->
                 {| items = co.Items; selectedIndex = co.SelectedIndex |})
               lineAnnotations =
                 region.LineAnnotations |> Array.map (fun a ->
                   {| line = a.Line
                      icon = SageFs.Features.LiveTesting.GutterIcon.toLabel a.Icon
                      tooltip = a.Tooltip |}) |})
        | None -> []
      let! standby = q.GetStandbyInfo ()
      let liveTestingStatus = q.GetLiveTestingStatus ()
      let payload =
        System.Text.Json.JsonSerializer.Serialize(
          {| sessionId = activeSid
             sessionState = SessionState.label state
             evalCount = stats.EvalCount
             avgMs = if stats.EvalCount > 0 then stats.TotalDuration.TotalMilliseconds / float stats.EvalCount else 0.0
             activeWorkingDir = activeDir
             standbyLabel = StandbyInfo.label standby
             liveTestingStatus = liveTestingStatus
             regions = regions |})
      do! ctx.Response.WriteAsync(sprintf "data: %s\n\n" payload)
      do! ctx.Response.Body.FlushAsync()
    }

    try
      do! pushJson ()
      match infra.StateChanged with
      | Some evt ->
        let tcs = Threading.Tasks.TaskCompletionSource()
        use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
        // Heartbeat keeps connection alive through proxies
        let heartbeat = new Threading.Timer((fun _ ->
            try
                let bytes = Text.Encoding.UTF8.GetBytes(": keepalive\n\n")
                ctx.Response.Body.WriteAsync(bytes).AsTask()
                |> fun t -> t.ContinueWith(fun (_: Threading.Tasks.Task) -> ctx.Response.Body.FlushAsync()) |> ignore
            with
            | :? System.IO.IOException | :? ObjectDisposedException -> ()
            | ex -> Log.error "[dashboard] Heartbeat error: %s" ex.Message), null, 15000, 15000)
        use _heartbeat = heartbeat
        use _sub = evt.Subscribe(fun _ ->
          Threading.Tasks.Task.Run(fun () ->
            task {
              try do! pushJson ()
              with
              | :? System.IO.IOException | :? ObjectDisposedException -> ()
              | ex -> Log.error "[dashboard] Push error: %s" ex.Message
            } :> Threading.Tasks.Task)
          |> ignore)
        do! tcs.Task
      | None ->
        while not ctx.RequestAborted.IsCancellationRequested do
          try
            do! Threading.Tasks.Task.Delay(Timeouts.sseEventInterval, ctx.RequestAborted)
            do! pushJson ()
          with
          | :? OperationCanceledException -> ()
          | _ -> () // Pipe broken or write error — ignore
    finally
      SageFs.Instrumentation.sseConnectionsActive.Add(-1L)
      infra.ConnectionTracker |> Option.iter (fun t -> t.Unregister(clientId))
  }

/// Parse an editor action string + optional value into an EditorAction DU case.
let parseEditorAction (actionName: string) (value: string option) : EditorAction option =
  match actionName with
  | "insertChar" ->
    value |> Option.bind (fun s -> if s.Length > 0 then Some (EditorAction.InsertChar s.[0]) else None)
  | "newLine" -> Some EditorAction.NewLine
  | "submit" -> Some EditorAction.Submit
  | "cancel" -> Some EditorAction.Cancel
  | "deleteBackward" -> Some EditorAction.DeleteBackward
  | "deleteForward" -> Some EditorAction.DeleteForward
  | "deleteWord" -> Some EditorAction.DeleteWord
  | "moveUp" -> Some (EditorAction.MoveCursor Direction.Up)
  | "moveDown" -> Some (EditorAction.MoveCursor Direction.Down)
  | "moveLeft" -> Some (EditorAction.MoveCursor Direction.Left)
  | "moveRight" -> Some (EditorAction.MoveCursor Direction.Right)
  | "setCursorPosition" ->
    value |> Option.bind (fun v ->
      let parts = (v : string).Split(',')
      match parts.Length = 2 with
      | false -> None
      | true ->
        match Int32.TryParse(parts.[0] : string), Int32.TryParse(parts.[1] : string) with
        | (true, line), (true, col) -> Some (EditorAction.SetCursorPosition (line, col))
        | _ -> None)
  | "moveWordForward" -> Some EditorAction.MoveWordForward
  | "moveWordBackward" -> Some EditorAction.MoveWordBackward
  | "moveToLineStart" -> Some EditorAction.MoveToLineStart
  | "moveToLineEnd" -> Some EditorAction.MoveToLineEnd
  | "undo" -> Some EditorAction.Undo
  | "selectAll" -> Some EditorAction.SelectAll
  | "triggerCompletion" -> Some EditorAction.TriggerCompletion
  | "dismissCompletion" -> Some EditorAction.DismissCompletion
  | "historyPrevious" -> Some EditorAction.HistoryPrevious
  | "historyNext" -> Some EditorAction.HistoryNext
  | "acceptCompletion" -> Some EditorAction.AcceptCompletion
  | "nextCompletion" -> Some EditorAction.NextCompletion
  | "previousCompletion" -> Some EditorAction.PreviousCompletion
  | "selectWord" -> Some EditorAction.SelectWord
  | "deleteToEndOfLine" -> Some EditorAction.DeleteToEndOfLine
  | "redo" -> Some EditorAction.Redo
  | "toggleSessionPanel" -> Some EditorAction.ToggleSessionPanel
  | "listSessions" -> Some EditorAction.ListSessions
  | "switchSession" -> value |> Option.map EditorAction.SwitchSession
  | "createSession" -> value |> Option.map (fun v -> EditorAction.CreateSession [v])
  | "stopSession" -> value |> Option.map EditorAction.StopSession
  | "historySearch" -> value |> Option.map EditorAction.HistorySearch
  | "resetSession" -> Some EditorAction.ResetSession
  | "hardResetSession" -> Some EditorAction.HardResetSession
  | "sessionNavUp" -> Some EditorAction.SessionNavUp
  | "sessionNavDown" -> Some EditorAction.SessionNavDown
  | "sessionSelect" -> Some EditorAction.SessionSelect
  | "sessionDelete" -> Some EditorAction.SessionDelete
  | "sessionStopOthers" -> Some EditorAction.SessionStopOthers
  | "clearOutput" -> Some EditorAction.ClearOutput
  | "sessionSetIndex" ->
    value |> Option.bind (fun s -> match Int32.TryParse(s) with true, i -> Some (EditorAction.SessionSetIndex i) | _ -> None)
  | "sessionCycleNext" -> Some EditorAction.SessionCycleNext
  | "sessionCyclePrev" -> Some EditorAction.SessionCyclePrev
  | "promptChar" ->
    value |> Option.bind (fun s -> if s.Length > 0 then Some (EditorAction.PromptChar s.[0]) else None)
  | "promptBackspace" -> Some EditorAction.PromptBackspace
  | "promptConfirm" -> Some EditorAction.PromptConfirm
  | "promptCancel" -> Some EditorAction.PromptCancel
  | _ -> None

/// Parse an app-level message, falling back to EditorAction wrapped in SageFsMsg.Editor.
let parseAppMsg (actionName: string) (editorAction: EditorAction option) : SageFsMsg option =
  match actionName with
  | "enableLiveTesting" -> Some SageFsMsg.EnableLiveTesting
  | "disableLiveTesting" -> Some SageFsMsg.DisableLiveTesting
  | "cycleRunPolicy" -> Some SageFsMsg.CycleRunPolicy
  | "toggleCoverage" -> Some SageFsMsg.ToggleCoverage
  | _ -> editorAction |> Option.map SageFsMsg.Editor

/// POST /api/dispatch — accept EditorAction JSON and dispatch to Elm runtime.
let createApiDispatchHandler
  (dispatch: SageFsMsg -> unit)
  : HttpHandler =
  fun ctx -> task {
    use reader = new StreamReader(ctx.Request.Body)
    let! body = reader.ReadToEndAsync()
    try
      let action = System.Text.Json.JsonSerializer.Deserialize<{| action: string; value: string option |}>(body)
      let editorAction = parseEditorAction action.action action.value
      let appMsg = parseAppMsg action.action editorAction
      match appMsg with
      | Some msg ->
        dispatch msg
        ctx.Response.StatusCode <- 200
        do! ctx.Response.WriteAsJsonAsync({| ok = true |})
      | None ->
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = sprintf "Unknown action: %s" action.action |})
    with ex ->
      ctx.Response.StatusCode <- 400
      do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
  }

/// Create all dashboard routes.
let createEndpoints
  (q: DashboardQueries)
  (a: DashboardActions)
  (infra: DashboardInfra)
  : HttpEndpoint list =
  [
    yield get "/dashboard" (FalcoResponse.ofHtml (renderShell infra.Version))
    yield get "/dashboard/stream" (createStreamHandler q infra)
    yield post "/dashboard/eval" (createEvalHandler a.EvalCode)
    yield post "/dashboard/eval-file" (createEvalFileHandler a.EvalCode)
    match infra.GetCompletions with
    | Some gc -> yield post "/dashboard/completions" (createCompletionsHandler gc)
    | None -> ()
    yield post "/dashboard/reset" (createResetHandler a.ResetSession)
    yield post "/dashboard/hard-reset" (createResetHandler a.HardResetSession)
    yield post "/dashboard/clear-output" createClearOutputHandler
    yield post "/dashboard/discover-projects" createDiscoverHandler
    yield post "/dashboard/set-theme" (fun ctx -> task {
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      try
        let req = System.Text.Json.JsonSerializer.Deserialize<{| theme: string |}>(body)
        let activeId = q.GetActiveSessionId ()
        let workingDir = q.GetSessionWorkingDir activeId
        match workingDir.Length > 0 && req.theme.Length > 0 with
        | true ->
          infra.SessionThemes.[workingDir] <- req.theme
          saveThemes DaemonState.SageFsDir infra.SessionThemes
        | false -> ()
        ctx.Response.StatusCode <- 200
        do! ctx.Response.WriteAsJsonAsync({| ok = true |})
      with ex ->
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsJsonAsync({| error = ex.Message |})
    })
    // Create session in temp directory
    match a.CreateSession with
    | Some handler ->
      yield post "/dashboard/session/create-temp" (fun ctx -> task {
        let tempDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-%s" (Guid.NewGuid().ToString("N").[..7]))
        Directory.CreateDirectory(tempDir) |> ignore
        Response.sseStartResponse ctx |> ignore
        let! result = handler [] tempDir
        match result with
        | Ok msg ->
          a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
          do! ssePatchNode ctx (
            Elem.div [ Attr.id "eval-result" ] [
              Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
                Text.raw msg
              ]
            ])
        | Error err ->
          do! ssePatchNode ctx (evalResultError err)
      })
    | None -> ()
    // Resume previous session (re-creates in same working dir)
    match a.CreateSession with
    | Some handler ->
      yield mapPost "/dashboard/session/resume/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sessionId -> fun ctx -> task {
          let! previous = q.GetPreviousSessions ()
          match previous |> List.tryFind (fun s -> s.Id = sessionId) with
          | Some prev ->
            Response.sseStartResponse ctx |> ignore
            let! result = handler prev.Projects prev.WorkingDir
            match result with
            | Ok msg ->
              a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
              do! ssePatchNode ctx (
                Elem.div [ Attr.id "eval-result" ] [
                  Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
                    Text.raw msg
                  ]
                ])
            | Error err ->
              do! ssePatchNode ctx (evalResultError err)
          | None ->
            Response.sseStartResponse ctx |> ignore
            do! ssePatchNode ctx (evalResultError (sprintf "Previous session '%s' not found" sessionId))
        })
    | None -> ()
    // TUI client API
    yield get "/api/state" (createApiStateHandler q infra)
    yield post "/api/dispatch" (createApiDispatchHandler a.Dispatch)
    match a.CreateSession with
    | Some handler ->
      yield post "/dashboard/session/create" (createCreateSessionHandler handler a.SwitchSession)
    | None -> ()
    match a.SwitchSession with
    | Some handler ->
      yield mapPost "/dashboard/session/switch/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sid -> createSessionActionHandler handler sid)
    | None -> ()
    match a.StopSession with
    | Some handler ->
      yield mapPost "/dashboard/session/stop/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sid -> createSessionActionHandler handler sid)
      yield post "/dashboard/session/stop-others" (fun ctx -> task {
        let! sessions = q.GetAllSessions ()
        let activeId = q.GetActiveSessionId ()
        let others =
          sessions
          |> List.filter (fun (s: WorkerProtocol.SessionInfo) -> s.Id <> activeId)
        for s in others do
          let! _ = handler s.Id
          ()
        a.Dispatch (SageFsMsg.Editor EditorAction.ListSessions)
        do! ctx.Response.WriteAsJsonAsync({| stopped = others.Length |})
      })
    | None -> ()
    // Daemon info endpoint for client discovery (replaces daemon.json)
    yield get "/api/daemon-info" (fun ctx -> task {
      let startedAt =
        let proc = System.Diagnostics.Process.GetCurrentProcess()
        proc.StartTime.ToUniversalTime()
      do! ctx.Response.WriteAsJsonAsync({|
        pid = Environment.ProcessId
        version = infra.Version
        startedAt = startedAt.ToString("o")
        workingDirectory = Environment.CurrentDirectory
      |})
    })
    // Graceful shutdown endpoint
    match a.ShutdownCallback with
    | Some shutdown ->
      yield post "/api/shutdown" (fun ctx -> task {
        do! ctx.Response.WriteAsJsonAsync({| status = "shutting_down" |})
        shutdown ()
      })
    | None -> ()
  ]

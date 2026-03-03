module SageFs.Vscode.InlineDecorations

open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers

// ── Configuration ──────────────────────────────────────────────

let getInlineTimeout () =
  let config = Workspace.getConfiguration "sagefs"
  config.get("inlineResultTimeout", 30000)

// ── Mutable state ──────────────────────────────────────────────

let mutable blockDecorations: Map<int, TextEditorDecorationType> = Map.empty
let mutable staleDecorations: Map<int, TextEditorDecorationType> = Map.empty

// ── Helpers ────────────────────────────────────────────────────

let formatDuration (ms: float) =
  if ms < 1000.0 then sprintf "%dms" (int ms)
  else sprintf "%.1fs" (ms / 1000.0)

// ── Core functions ─────────────────────────────────────────────

let clearBlockDecoration (line: int) =
  match Map.tryFind line blockDecorations with
  | Some deco ->
    deco.dispose () |> ignore
    blockDecorations <- Map.remove line blockDecorations
  | None -> ()
  match Map.tryFind line staleDecorations with
  | Some deco ->
    deco.dispose () |> ignore
    staleDecorations <- Map.remove line staleDecorations
  | None -> ()

let autoClearAfter (line: int) =
  let ms = getInlineTimeout ()
  match ms with
  | 0 -> ()
  | _ -> jsSetTimeout (fun () -> clearBlockDecoration line) ms |> ignore

let clearAllDecorations () =
  blockDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  blockDecorations <- Map.empty
  staleDecorations |> Map.iter (fun _ deco -> deco.dispose () |> ignore)
  staleDecorations <- Map.empty

let markDecorationsStale (editor: TextEditor) =
  let lines = blockDecorations |> Map.toList |> List.map fst
  for line in lines do
    match Map.tryFind line blockDecorations with
    | Some deco ->
      deco.dispose () |> ignore
      blockDecorations <- Map.remove line blockDecorations
      if not (Map.containsKey line staleDecorations) then
        let staleOpts = createObj [
          "after" ==> createObj [
            "contentText" ==> "  // ⏸ stale"
            "color" ==> newThemeColor "sagefs.staleForeground"
            "fontStyle" ==> "italic"
          ]
        ]
        let staleDeco = Window.createTextEditorDecorationType staleOpts
        let lineText = editor.document.lineAt(float line).text
        let endCol = lineText.Length
        let range = newRange line endCol line endCol
        editor.setDecorations(staleDeco, ResizeArray [| box range |])
        staleDecorations <- Map.add line staleDeco staleDecorations
    | None -> ()

/// Get the line number for inline decoration placement.
let private getEditorLine (editor: TextEditor) =
  if editor.selection.isEmpty
  then int editor.selection.active.line
  else int editor.selection.``end``.line

let showInlineResult (editor: TextEditor) (text: string) (durationMs: float option) =
  let trimmed = text.Trim()
  match trimmed with
  | "" -> ()
  | _ ->
    let line = getEditorLine editor
    clearBlockDecoration line
    let lines = trimmed.Split('\n')
    let firstLine = match lines.Length with 0 -> "" | _ -> lines.[0]
    let durSuffix =
      match durationMs with
      | Some ms -> sprintf "  %s" (formatDuration ms)
      | None -> ""
    let contentText =
      match lines.Length with
      | 0 | 1 ->
        sprintf "  // → %s%s" firstLine durSuffix
      | n ->
        let summary =
          if n <= 4 then lines |> String.concat "  │  "
          else sprintf "%s  │  ... (%d lines)" firstLine n
        sprintf "  // → %s%s" summary durSuffix
    let opts = createObj [
      "after" ==> createObj [
        "contentText" ==> contentText
        "color" ==> newThemeColor "sagefs.successForeground"
        "fontStyle" ==> "italic"
      ]
    ]
    let deco = Window.createTextEditorDecorationType opts
    let lineText = editor.document.lineAt(float line).text
    let endCol = lineText.Length
    let range = newRange line endCol line endCol
    editor.setDecorations(deco, ResizeArray [| box range |])
    blockDecorations <- Map.add line deco blockDecorations
    autoClearAfter line

let showInlineDiagnostic (editor: TextEditor) (text: string) =
  let firstLine =
    let parts = text.Split('\n')
    match parts.Length with 0 -> "" | _ -> parts.[0].Trim()
  match firstLine with
  | "" -> ()
  | _ ->
    let line = getEditorLine editor
    clearBlockDecoration line
    let opts = createObj [
      "after" ==> createObj [
        "contentText" ==> sprintf "  // ❌ %s" firstLine
        "color" ==> newThemeColor "sagefs.errorForeground"
        "fontStyle" ==> "italic"
      ]
    ]
    let deco = Window.createTextEditorDecorationType opts
    let lineText = editor.document.lineAt(float line).text
    let endCol = lineText.Length
    let range = newRange line endCol line endCol
    editor.setDecorations(deco, ResizeArray [| box range |])
    blockDecorations <- Map.add line deco blockDecorations
    autoClearAfter line

module SageFs.Vscode.CodeLensProvider

open Fable.Core
open Fable.Core.JsInterop
open Vscode

/// Creates a CodeLens provider object compatible with VSCode's API.
/// Shows "▶ Eval" at the start of each code block — either ;; delimited or blank-line separated.
/// Respects density setting: disabled in Minimal and Normal modes.
let create () =
  createObj [
    "provideCodeLenses" ==> fun (doc: TextDocument) (_token: obj) ->
      let cfg = Workspace.getConfiguration "sagefs"
      let density = cfg.get("density", "full")
      match density with
      | "minimal" | "normal" -> [||]
      | _ ->
      let text = doc.getText ()
      let lines = text.Split('\n')
      let lenses = ResizeArray<CodeLens>()
      let hasSemiSemi = lines |> Array.exists (fun l -> l.TrimEnd().EndsWith(";;"))
      match hasSemiSemi with
      | true ->
        // ;; mode: lens at start of each ;; delimited block
        let mutable blockStart = 0
        for i in 0 .. lines.Length - 1 do
          let line = lines.[i].TrimEnd()
          match line.EndsWith(";;") with
          | true ->
            let range = newRange blockStart 0 blockStart 0
            let cmd = createObj [
              "title" ==> "▶ Eval"
              "command" ==> "sagefs.eval"
              "arguments" ==> [| box blockStart |]
            ]
            lenses.Add(newCodeLens range cmd)
            blockStart <- i + 1
          | false -> ()
      | false ->
        // Blank-line mode: lens at start of each non-empty paragraph
        let mutable inBlock = false
        for i in 0 .. lines.Length - 1 do
          let empty = lines.[i].Trim() = ""
          match empty, inBlock with
          | false, false ->
            let range = newRange i 0 i 0
            let cmd = createObj [
              "title" ==> "▶ Eval"
              "command" ==> "sagefs.eval"
              "arguments" ==> [| box i |]
            ]
            lenses.Add(newCodeLens range cmd)
            inBlock <- true
          | true, true -> inBlock <- false
          | _ -> ()
      lenses.ToArray()
  ]

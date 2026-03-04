module SageFs.Tests.PushEventTests

open Expecto
open Expecto.Flip
open SageFs.McpPushNotifications
open SageFs.Features.LiveTesting

let private emptySummary =
  { Total = 0; Passed = 0; Failed = 0; Stale = 0
    Running = 0; Disabled = 0; Enabled = true }

let private emptyBatch =
  { Generation = RunGeneration 0; Freshness = ResultFreshness.Fresh
    Completion = BatchCompletion.Complete (0, 0)
    Entries = [||]; Summary = emptySummary }

let private emptyAnnotations =
  { FilePath = ""; TestAnnotations = [||]; CoverageAnnotations = [||]
    InlineFailures = [||]; CodeLenses = [||] }

let private allPushEvents = [
  PushEvent.DiagnosticsChanged []
  PushEvent.StateChanged (0, 0)
  PushEvent.FileReloaded "test.fs"
  PushEvent.SessionFaulted "err"
  PushEvent.WarmupCompleted
  PushEvent.TestSummaryChanged emptySummary
  PushEvent.TestResultsBatch emptyBatch
  PushEvent.FileAnnotationsUpdated emptyAnnotations
]

[<Tests>]
let pushEventTagTests = testList "PushEvent.tag" [
  testCase "all variants have unique tags" (fun () ->
    let tags = allPushEvents |> List.map PushEvent.tag
    tags |> List.distinct |> Expect.hasLength "all unique" allPushEvents.Length)
  testCase "tags are sequential 0..7" (fun () ->
    let tags = allPushEvents |> List.map PushEvent.tag
    tags |> Expect.equal "sequential" [0;1;2;3;4;5;6;7])
]

[<Tests>]
let pushEventMergeStrategyTests = testList "PushEvent.mergeStrategy" [
  testCase "FileReloaded accumulates" (fun () ->
    PushEvent.mergeStrategy (PushEvent.FileReloaded "f.fs")
    |> Expect.equal "accumulate" MergeStrategy.Accumulate)
  testCase "all others replace" (fun () ->
    let nonFile =
      allPushEvents
      |> List.filter (fun e ->
        match e with PushEvent.FileReloaded _ -> false | _ -> true)
    for e in nonFile do
      PushEvent.mergeStrategy e
      |> Expect.equal (sprintf "replace for tag %d" (PushEvent.tag e)) MergeStrategy.Replace)
]

[<Tests>]
let pushEventFormatForLlmTests = testList "PushEvent.formatForLlm" [
  testCase "diagnostics cleared" (fun () ->
    PushEvent.formatForLlm (PushEvent.DiagnosticsChanged [])
    |> Expect.stringContains "cleared" "cleared")
  testCase "diagnostics with errors" (fun () ->
    let errors = [("src/Main.fs", 10, "Type mismatch")]
    PushEvent.formatForLlm (PushEvent.DiagnosticsChanged errors)
    |> Expect.stringContains "has count" "1 diagnostic")
  testCase "diagnostics truncates at 5" (fun () ->
    let errors = [for i in 1..8 -> (sprintf "file%d.fs" i, i, sprintf "error %d" i)]
    let result = PushEvent.formatForLlm (PushEvent.DiagnosticsChanged errors)
    result |> Expect.stringContains "shows truncation" "3 more")
  testCase "state changed" (fun () ->
    PushEvent.formatForLlm (PushEvent.StateChanged (42, 7))
    |> Expect.stringContains "output count" "output=42")
  testCase "file reloaded shows filename only" (fun () ->
    PushEvent.formatForLlm (PushEvent.FileReloaded "/long/path/to/Module.fs")
    |> Expect.stringContains "filename" "Module.fs")
  testCase "session faulted" (fun () ->
    PushEvent.formatForLlm (PushEvent.SessionFaulted "out of memory")
    |> Expect.stringContains "error msg" "out of memory")
  testCase "warmup completed" (fun () ->
    PushEvent.formatForLlm PushEvent.WarmupCompleted
    |> Expect.stringContains "warmup" "warmup")
  testCase "test summary" (fun () ->
    let s = { emptySummary with Total = 100; Passed = 95; Failed = 5 }
    PushEvent.formatForLlm (PushEvent.TestSummaryChanged s)
    |> Expect.stringContains "total" "100 total")
]

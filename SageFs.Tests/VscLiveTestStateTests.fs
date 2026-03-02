module SageFs.Tests.VscLiveTestStateTests

open Expecto
open FsCheck
open SageFs.Vscode.LiveTestingTypes

// ── Generators ──

let genNonNullString =
  Gen.choose (3, 12)
  |> Gen.bind (fun len ->
    Gen.arrayOfLength len (Gen.choose (97, 122) |> Gen.map char)
    |> Gen.map (fun cs -> System.String(cs)))

let genTestId =
  genNonNullString |> Gen.map VscTestId.create

let genOutcome =
  Gen.oneof [
    Gen.constant VscTestOutcome.Passed
    genNonNullString |> Gen.map VscTestOutcome.Failed
    genNonNullString |> Gen.map VscTestOutcome.Skipped
    Gen.constant VscTestOutcome.Running
    genNonNullString |> Gen.map VscTestOutcome.Errored
    Gen.constant VscTestOutcome.Stale
    Gen.constant VscTestOutcome.PolicyDisabled
  ]

let genTestInfo =
  Gen.map2 (fun id name ->
    { Id = id; DisplayName = name; FullName = name; FilePath = None; Line = None }
  ) genTestId genNonNullString

let genTestResult =
  Gen.map2 (fun id outcome ->
    { Id = id; Outcome = outcome; DurationMs = Some 1.0; Output = None }
  ) genTestId genOutcome

let genFreshness =
  Gen.oneof [
    Gen.constant VscResultFreshness.Fresh
    Gen.constant VscResultFreshness.StaleCodeEdited
    Gen.constant VscResultFreshness.StaleWrongGeneration
  ]

let genStaleFreshness =
  Gen.oneof [
    Gen.constant VscResultFreshness.StaleCodeEdited
    Gen.constant VscResultFreshness.StaleWrongGeneration
  ]

let genCategory =
  Gen.oneof [
    Gen.constant VscTestCategory.Unit
    Gen.constant VscTestCategory.Integration
    Gen.constant VscTestCategory.Browser
    Gen.constant VscTestCategory.Benchmark
    Gen.constant VscTestCategory.Architecture
    Gen.constant VscTestCategory.Property
  ]

let genPolicy =
  Gen.oneof [
    Gen.constant VscRunPolicy.EveryKeystroke
    Gen.constant VscRunPolicy.OnSave
    Gen.constant VscRunPolicy.OnDemand
    Gen.constant VscRunPolicy.Disabled
  ]

let genEvent =
  Gen.oneof [
    Gen.sample 5 genTestInfo |> Gen.constant |> Gen.map VscLiveTestEvent.TestsDiscovered
    Gen.sample 5 genTestId |> Gen.constant |> Gen.map VscLiveTestEvent.TestRunStarted
    Gen.map2 (fun rs f -> VscLiveTestEvent.TestResultBatch (rs, f))
      (Gen.constant (Gen.sample 5 genTestResult))
      genFreshness
    Gen.constant VscLiveTestEvent.LiveTestingEnabled
    Gen.constant VscLiveTestEvent.LiveTestingDisabled
    Gen.map2 (fun c p -> VscLiveTestEvent.RunPolicyChanged (c, p)) genCategory genPolicy
    Gen.map3 (fun a b c -> VscLiveTestEvent.TestCycleTimingRecorded (a, b, c))
      (Gen.map float (Gen.choose (0, 1000)))
      (Gen.map float (Gen.choose (0, 5000)))
      (Gen.map float (Gen.choose (0, 10000)))
  ]

// ── Helpers ──

let foldEvents events state =
  events |> Array.fold (fun (s, allChanges) evt ->
    let s', changes = VscLiveTestState.update evt s
    s', allChanges @ changes
  ) (state, [])

let cfg = { FsCheckConfig.defaultConfig with maxTest = 200 }

// ── Tests ──

[<Tests>]
let tests = testList "VscLiveTestState property tests" [
  testPropertyWithConfig cfg "update never throws on arbitrary events" (fun () ->
    let events = Gen.sample 10 genEvent
    let mutable state = VscLiveTestState.empty
    for evt in events do
      let s', _ = VscLiveTestState.update evt state
      state <- s'
    true)

  testPropertyWithConfig cfg "summary counts are never negative" (fun () ->
    let events = Gen.sample 10 genEvent
    let state, _ = foldEvents events VscLiveTestState.empty
    let s = VscLiveTestState.summary state
    s.Total >= 0 && s.Passed >= 0 && s.Failed >= 0
    && s.Running >= 0 && s.Stale >= 0 && s.Disabled >= 0)

  testPropertyWithConfig cfg "TestsDiscovered adds all tests to map" (fun () ->
    let tests = Gen.sample 5 genTestInfo
    let evt = VscLiveTestEvent.TestsDiscovered tests
    let state', _ = VscLiveTestState.update evt VscLiveTestState.empty
    tests |> Array.forall (fun t -> Map.containsKey t.Id state'.Tests))

  testPropertyWithConfig cfg "TestRunStarted populates RunningTests" (fun () ->
    let ids = Gen.sample 5 genTestId |> Array.distinct
    let evt = VscLiveTestEvent.TestRunStarted ids
    let state', _ = VscLiveTestState.update evt VscLiveTestState.empty
    ids |> Array.forall (fun id -> Set.contains id state'.RunningTests))

  testPropertyWithConfig cfg "TestRunStarted resets freshness to Fresh" (fun () ->
    let ids = Gen.sample 3 genTestId
    let stale = { VscLiveTestState.empty with Freshness = VscResultFreshness.StaleCodeEdited }
    let state', _ = VscLiveTestState.update (VscLiveTestEvent.TestRunStarted ids) stale
    state'.Freshness = VscResultFreshness.Fresh)

  testPropertyWithConfig cfg "TestResultBatch removes completed from RunningTests" (fun () ->
    let ids = Gen.sample 5 genTestId |> Array.distinct
    let results = ids |> Array.map (fun id ->
      { Id = id; Outcome = VscTestOutcome.Passed; DurationMs = Some 1.0; Output = None })
    let started = { VscLiveTestState.empty with RunningTests = Set.ofArray ids }
    let state', _ =
      VscLiveTestState.update
        (VscLiveTestEvent.TestResultBatch (results, VscResultFreshness.Fresh))
        started
    state'.RunningTests.IsEmpty)

  testPropertyWithConfig cfg "enable/disable toggles Enabled field" (fun () ->
    let s1, _ = VscLiveTestState.update VscLiveTestEvent.LiveTestingEnabled VscLiveTestState.empty
    let s2, _ = VscLiveTestState.update VscLiveTestEvent.LiveTestingDisabled s1
    s1.Enabled = VscLiveTestingEnabled.LiveTestingOn
    && s2.Enabled = VscLiveTestingEnabled.LiveTestingOff)

  testPropertyWithConfig cfg "RunPolicyChanged updates Policies map" (fun () ->
    let cat = Gen.sample 1 genCategory |> Array.head
    let pol = Gen.sample 1 genPolicy |> Array.head
    let state', _ =
      VscLiveTestState.update
        (VscLiveTestEvent.RunPolicyChanged (cat, pol))
        VscLiveTestState.empty
    Map.tryFind cat state'.Policies = Some pol)

  testPropertyWithConfig cfg "TestCycleTimingRecorded stores LastTiming" (fun () ->
    let ts = Gen.sample 1 (Gen.map float (Gen.choose (0, 1000))) |> Array.head
    let fcs = Gen.sample 1 (Gen.map float (Gen.choose (0, 5000))) |> Array.head
    let exec = Gen.sample 1 (Gen.map float (Gen.choose (0, 10000))) |> Array.head
    let state', _ =
      VscLiveTestState.update
        (VscLiveTestEvent.TestCycleTimingRecorded (ts, fcs, exec))
        VscLiveTestState.empty
    state'.LastTiming = Some (ts, fcs, exec))

  testPropertyWithConfig cfg "update always emits at least one change" (fun () ->
    let evt = Gen.sample 5 genEvent |> Array.head
    let _, changes = VscLiveTestState.update evt VscLiveTestState.empty
    changes.Length >= 1)

  testCase "empty state produces zero summary" (fun () ->
    let s = VscLiveTestState.summary VscLiveTestState.empty
    s.Total |> Expect.equal "total 0" 0
    s.Passed |> Expect.equal "passed 0" 0
    s.Failed |> Expect.equal "failed 0" 0
    s.Running |> Expect.equal "running 0" 0
    s.Stale |> Expect.equal "stale 0" 0
    s.Disabled |> Expect.equal "disabled 0" 0)

  testPropertyWithConfig cfg "stale batch emits ResultsStale change" (fun () ->
    let results = Gen.sample 3 genTestResult
    let freshness = Gen.sample 1 genStaleFreshness |> Array.head
    let _, changes =
      VscLiveTestState.update
        (VscLiveTestEvent.TestResultBatch (results, freshness))
        VscLiveTestState.empty
    changes |> List.exists (fun c ->
      match c with VscStateChange.ResultsStale _ -> true | _ -> false))

  testPropertyWithConfig cfg "fresh batch does not emit ResultsStale" (fun () ->
    let results = Gen.sample 3 genTestResult
    let _, changes =
      VscLiveTestState.update
        (VscLiveTestEvent.TestResultBatch (results, VscResultFreshness.Fresh))
        VscLiveTestState.empty
    changes |> List.forall (fun c ->
      match c with VscStateChange.ResultsStale _ -> false | _ -> true))

  testPropertyWithConfig cfg "discovering same tests twice is idempotent on count" (fun () ->
    let tests = Gen.sample 5 genTestInfo
    let evt = VscLiveTestEvent.TestsDiscovered tests
    let s1, _ = VscLiveTestState.update evt VscLiveTestState.empty
    let s2, _ = VscLiveTestState.update evt s1
    s1.Tests.Count = s2.Tests.Count)

  testPropertyWithConfig cfg "summary Total equals Tests.Count" (fun () ->
    let events = Gen.sample 10 genEvent
    let state, _ = foldEvents events VscLiveTestState.empty
    let s = VscLiveTestState.summary state
    s.Total = state.Tests.Count)

  testPropertyWithConfig cfg "summary Running equals RunningTests count" (fun () ->
    let events = Gen.sample 10 genEvent
    let state, _ = foldEvents events VscLiveTestState.empty
    let s = VscLiveTestState.summary state
    s.Running = state.RunningTests.Count)
]

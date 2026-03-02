module SageFs.WarmUp

/// Individual FCS diagnostic captured during a warmup open failure.
type WarmupFcsDiagnostic = {
  Message: string
  Severity: string
  ErrorNumber: int
  FileName: string option
  StartLine: int
  EndLine: int
  StartColumn: int
  EndColumn: int
}

/// Represents a namespace or module that was opened during warmup.
type OpenedBinding = {
  Name: string
  IsModule: bool
  Source: string
  DurationMs: float
}

/// Rich failure info for a single open that did not succeed.
type WarmupOpenFailure = {
  Name: string
  IsModule: bool
  ErrorMessage: string
  Diagnostics: WarmupFcsDiagnostic list
  RetryCount: int
  DurationMs: float
}

/// Phase timing breakdown for warmup.
type WarmupPhaseTiming = {
  ScanSourceFilesMs: int64
  ScanAssembliesMs: int64
  OpenNamespacesMs: int64
  TotalMs: int64
}

/// Returns true if the FSI open error is benign — the module resolved but
/// can't be opened (e.g. RequireQualifiedAccess). Types are still accessible
/// via qualified paths, so this isn't a real failure.
let isBenignOpenError (errorMsg: string) : bool =
  errorMsg.Contains("RequireQualifiedAccess")

/// Result of attempting to open a single namespace/module.
type OpenAttemptResult =
  | OpenSuccess of durationMs: float
  | OpenFailed of errorMessage: string * diagnostics: WarmupFcsDiagnostic list * durationMs: float

/// Opens names iteratively with rich failure info.
/// opener: tries to open a name+isModule, returns OpenAttemptResult.
/// Returns (succeeded with timing, failures with diagnostics).
let openWithRetryRich
  (maxRounds: int)
  (opener: string -> bool -> OpenAttemptResult)
  (names: (string * bool) list)
  : OpenedBinding list * WarmupOpenFailure list =
  let firstErrors = System.Collections.Generic.Dictionary<string, string * WarmupFcsDiagnostic list>()
  let retryCounts = System.Collections.Generic.Dictionary<string, int>()
  let rec loop round remaining acc =
    match round > maxRounds || List.isEmpty remaining with
    | true ->
      let failures =
        remaining |> List.map (fun (n, isMod) ->
          let errMsg, diags =
            match firstErrors.TryGetValue(n) with
            | true, (e, d) -> e, d
            | _ -> "max retries exceeded", []
          let retries = match retryCounts.TryGetValue(n) with | true, c -> c | _ -> 0
          { Name = n; IsModule = isMod; ErrorMessage = errMsg
            Diagnostics = diags; RetryCount = retries; DurationMs = 0.0 })
      (acc, failures)
    | false ->
      let results =
        remaining |> List.map (fun (name, isMod) ->
          let r = opener name isMod
          match retryCounts.ContainsKey(name) with
          | true -> retryCounts.[name] <- retryCounts.[name] + 1
          | false -> retryCounts.[name] <- 1
          (name, isMod, r))
      let succeeded =
        results |> List.choose (fun (n, isMod, r) ->
          match r with
          | OpenSuccess ms ->
            Some { Name = n; IsModule = isMod; Source = "warmup"; DurationMs = ms }
          | _ -> None)
      let failed =
        results |> List.choose (fun (n, isMod, r) ->
          match r with
          | OpenFailed (err, diags, _) ->
            match firstErrors.ContainsKey(n) with
            | false -> firstErrors.[n] <- (err, diags)
            | true -> ()
            Some (n, isMod)
          | _ -> None)
      match List.isEmpty succeeded with
      | true ->
        let failures =
          failed |> List.map (fun (n, isMod) ->
            let errMsg, diags =
              match firstErrors.TryGetValue(n) with | true, (e, d) -> e, d | _ -> "unknown", []
            let retries = match retryCounts.TryGetValue(n) with | true, c -> c | _ -> 0
            { Name = n; IsModule = isMod; ErrorMessage = errMsg
              Diagnostics = diags; RetryCount = retries; DurationMs = 0.0 })
        (acc, failures)
      | false ->
        loop (round + 1) failed (acc @ succeeded)
  loop 1 names []

/// Legacy adapter: Opens names iteratively, retrying failures until convergence.
/// Returns (succeeded, permanentFailures) where permanentFailures = (name, firstError).
let openWithRetry
  (maxRounds: int)
  (opener: string -> Result<unit, string>)
  (names: string list)
  : string list * (string * string) list =
  let firstErrors = System.Collections.Generic.Dictionary<string, string>()
  let rec loop round remaining acc =
    match round > maxRounds || List.isEmpty remaining with
    | true ->
      (acc, remaining |> List.map (fun n ->
        match firstErrors.TryGetValue(n) with
        | true, e -> n, e
        | _ -> n, "max retries exceeded"))
    | false ->
      let results =
        remaining
        |> List.map (fun name -> name, opener name)
      let succeeded =
        results
        |> List.choose (fun (n, r) ->
          match r with Ok () -> Some n | _ -> None)
      let failed =
        results
        |> List.choose (fun (n, r) ->
          match r with
          | Error e ->
            match firstErrors.ContainsKey(n) with
            | false -> firstErrors.[n] <- e
            | true -> ()
            Some (n, e)
          | _ -> None)
      match List.isEmpty succeeded with
      | true ->
        (acc, failed |> List.map (fun (n, _) ->
          n, (match firstErrors.TryGetValue(n) with true, e -> e | _ -> "unknown")))
      | false ->
        loop (round + 1) (failed |> List.map fst) (acc @ succeeded)
  loop 1 names []

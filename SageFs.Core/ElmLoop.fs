namespace SageFs

/// Core Elm Architecture types — the contract every frontend depends on
type Update<'Model, 'Msg, 'Effect> =
  'Msg -> 'Model -> 'Model * 'Effect list

type Render<'Model, 'Region> =
  'Model -> 'Region list

type EffectHandler<'Msg, 'Effect> =
  ('Msg -> unit) -> 'Effect -> Async<unit>

/// An Elm Architecture program definition
type ElmProgram<'Model, 'Msg, 'Effect, 'Region> = {
  Update: Update<'Model, 'Msg, 'Effect>
  Render: Render<'Model, 'Region>
  ExecuteEffect: EffectHandler<'Msg, 'Effect>
  OnModelChanged: 'Model -> 'Region list -> unit
}

/// The running Elm loop — dispatch messages and read current state.
type ElmRuntime<'Model, 'Msg, 'Region> = {
  Dispatch: 'Msg -> unit
  GetModel: unit -> 'Model
  GetRegions: unit -> 'Region list
}

module ElmLoop =
  open System.Diagnostics
  open System.Collections.Concurrent
  open System.Threading
  open SageFs.Utils

  let private kvp k v = System.Collections.Generic.KeyValuePair(k, v :> obj)

  /// Start the Elm loop with an initial model.
  /// Uses a dedicated drain thread (not thread pool) to avoid starvation.
  /// Dispatch enqueues + signals; the drain thread wakes, processes all
  /// pending messages, renders ONCE, then sleeps until signalled again.
  let start (program: ElmProgram<'Model, 'Msg, 'Effect, 'Region>)
            (initialModel: 'Model) : ElmRuntime<'Model, 'Msg, 'Region> =
    let mutable model = initialModel
    let mutable latestRegions = []
    let lockObj = obj ()
    let queue = ConcurrentQueue<'Msg>()
    // Signal for the dedicated drain thread — Set() wakes it, Wait() sleeps it
    let signal = new ManualResetEventSlim(false)

    /// Drain all queued messages, render once, push once.
    /// Runs exclusively on the dedicated drain thread.
    let drain () =
      let batchSw = Stopwatch.StartNew()
      let batchTag = kvp "msg_type" "batch"

      // Phase 1: Drain queue — apply all updates under model lock
      let prevModel, snapshot, allEffects, batchCount =
        lock lockObj (fun () ->
          let prev = model
          let mutable effs = []
          let mutable count = 0
          let mutable item = Unchecked.defaultof<'Msg>
          while queue.TryDequeue(&item) do
            count <- count + 1
            Instrumentation.elmDispatchCount.Add(1L)
            let updateSw = Stopwatch.StartNew()
            try
              let m, msgEffs = program.Update item model
              model <- m
              effs <- msgEffs @ effs
            with ex ->
              Instrumentation.elmloopErrors.Add(1L, kvp "phase" "update")
              Log.error "[ElmLoop] Update threw for %s: %s" (item.GetType().Name) ex.Message
            updateSw.Stop()
            Instrumentation.elmloopUpdateMs.Record(updateSw.Elapsed.TotalMilliseconds, kvp "msg_type" (item.GetType().Name))
          prev, model, List.rev effs, count)

      match batchCount with
      | 0 -> ()
      | _ ->

      let modelChanged = not (obj.ReferenceEquals(prevModel, snapshot))

      let activity = Instrumentation.elmloopSource.StartActivity("elm.batch")
      match isNull activity with
      | false ->
        activity.SetTag("elm.batch_size", batchCount) |> ignore
        activity.SetTag("elm.model_changed", modelChanged) |> ignore
      | true -> ()

      // Phase 2: Render once for entire batch (outside model lock)
      let renderSw = Stopwatch.StartNew()
      let regions =
        match modelChanged with
        | true ->
          try program.Render snapshot
          with ex ->
            Instrumentation.elmloopErrors.Add(1L, kvp "phase" "render")
            Log.error "[ElmLoop] Render threw: %s" ex.Message
            lock lockObj (fun () -> latestRegions)
        | false ->
          lock lockObj (fun () -> latestRegions)
      renderSw.Stop()
      Instrumentation.elmloopRenderMs.Record(renderSw.Elapsed.TotalMilliseconds, batchTag)

      lock lockObj (fun () -> latestRegions <- regions)

      // Phase 3: Callback (SSE push) once for entire batch
      let cbSw = Stopwatch.StartNew()
      match modelChanged with
      | true ->
        try program.OnModelChanged snapshot regions
        with ex ->
          Instrumentation.elmloopErrors.Add(1L, kvp "phase" "callback")
          Log.error "[ElmLoop] OnModelChanged threw: %s" ex.Message
      | false -> ()
      cbSw.Stop()
      Instrumentation.elmloopCallbackMs.Record(cbSw.Elapsed.TotalMilliseconds, batchTag)

      // Phase 4: Spawn effects with parent trace context from batch span
      match allEffects.IsEmpty with
      | false -> Instrumentation.elmloopEffectsSpawned.Add(int64 allEffects.Length)
      | true -> ()
      let parentCtx =
        match isNull activity with
        | false -> activity.Context
        | true -> System.Diagnostics.ActivityContext()
      let hasParent =
        parentCtx.TraceId.ToString() <> "00000000000000000000000000000000"
      for effect in allEffects do
        Async.Start (async {
          let effectActivity =
            match hasParent with
            | true ->
              Instrumentation.elmloopSource.StartActivity(
                "elm.effect",
                ActivityKind.Internal,
                parentCtx)
            | false -> null
          try
            do! program.ExecuteEffect (fun msg -> queue.Enqueue msg; signal.Set()) effect
            Instrumentation.succeedSpan effectActivity
          with ex ->
            Instrumentation.elmloopErrors.Add(1L, kvp "phase" "effect")
            Log.error "[ElmLoop] Effect threw: %s" ex.Message
            Instrumentation.failSpan effectActivity ex.Message
        })

      batchSw.Stop()
      let totalMs = batchSw.Elapsed.TotalMilliseconds
      Instrumentation.elmloopTotalDispatchMs.Record(totalMs, batchTag)

      match isNull activity with
      | false ->
        activity.SetTag("elm.render_ms", renderSw.Elapsed.TotalMilliseconds) |> ignore
        activity.SetTag("elm.callback_ms", cbSw.Elapsed.TotalMilliseconds) |> ignore
        activity.SetTag("elm.total_ms", totalMs) |> ignore
        activity.SetTag("elm.effects_count", allEffects.Length) |> ignore
        activity.Stop()
        activity.Dispose()
      | true -> ()

      match totalMs > 50.0 with
      | true ->
        Log.warn "[ElmLoop] SLOW batch (%d msgs): %.1fms (render=%.1fms cb=%.1fms changed=%b)"
          batchCount totalMs renderSw.Elapsed.TotalMilliseconds cbSw.Elapsed.TotalMilliseconds modelChanged
      | false -> ()

    // Dedicated drain thread — runs outside the thread pool so it's never
    // starved by Kestrel/SSE/effect work saturating the pool.
    let drainThread = Thread(fun () ->
      while true do
        signal.Wait()
        signal.Reset()
        // Drain until queue is truly empty (messages may arrive during processing)
        while not queue.IsEmpty do
          drain ())
    drainThread.IsBackground <- true
    drainThread.Name <- "ElmLoop-Drain"
    drainThread.Start()

    let dispatch (msg: 'Msg) =
      queue.Enqueue msg
      signal.Set()

    let regions =
      try program.Render initialModel
      with ex ->
        Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "initial_render" :> obj))
        Log.error "[ElmLoop] Initial Render threw: %s" ex.Message
        []
    latestRegions <- regions
    try program.OnModelChanged initialModel regions
    with ex ->
      Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "initial_callback" :> obj))
      Log.error "[ElmLoop] Initial OnModelChanged threw: %s" ex.Message

    { Dispatch = dispatch
      GetModel = fun () -> lock lockObj (fun () -> model)
      GetRegions = fun () -> lock lockObj (fun () -> latestRegions) }

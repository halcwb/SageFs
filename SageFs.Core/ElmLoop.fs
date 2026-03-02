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
  /// Uses Elmish-style message queue: dispatch enqueues, single drainer
  /// processes all pending messages then renders ONCE.
  let start (program: ElmProgram<'Model, 'Msg, 'Effect, 'Region>)
            (initialModel: 'Model) : ElmRuntime<'Model, 'Msg, 'Region> =
    let mutable model = initialModel
    let mutable latestRegions = []
    let lockObj = obj ()
    let queue = ConcurrentQueue<'Msg>()
    let mutable draining = 0

    /// Drain all queued messages, render once, push once.
    /// Called by exactly one thread at a time (guarded by Interlocked CAS).
    let rec drain () =
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

      // Phase 4: Spawn effects
      match allEffects.IsEmpty with
      | false -> Instrumentation.elmloopEffectsSpawned.Add(int64 allEffects.Length)
      | true -> ()
      for effect in allEffects do
        Async.Start (async {
          try do! program.ExecuteEffect (fun msg -> queue.Enqueue msg; tryDrain()) effect
          with ex ->
            Instrumentation.elmloopErrors.Add(1L, kvp "phase" "effect")
            Log.error "[ElmLoop] Effect threw: %s" ex.Message
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

      // If messages arrived during render/callback, drain again
      match queue.IsEmpty with
      | false -> drain ()
      | true -> ()

    /// Try to become the drainer. If another thread is already draining,
    /// return immediately — our message is in the queue and will be picked up.
    and tryDrain () =
      match Interlocked.CompareExchange(&draining, 1, 0) with
      | 0 ->
        try drain ()
        finally
          Volatile.Write(&draining, 0)
          // Final check: messages may have arrived after drain() saw empty queue
          // but before we cleared the flag. One more attempt prevents lost messages.
          match queue.IsEmpty with
          | true -> ()
          | false ->
            match Interlocked.CompareExchange(&draining, 1, 0) with
            | 0 ->
              try drain ()
              finally Volatile.Write(&draining, 0)
            | _ -> ()
      | _ -> () // Another thread is draining; our message will be processed

    let dispatch (msg: 'Msg) =
      queue.Enqueue msg
      tryDrain ()

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

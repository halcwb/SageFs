module SageFs.EventStore

#nowarn "44" // Marten deprecates GeneratedCodeMode, but CritterStackDefaults() requires DI

open System
open System.IO
open Marten
open SageFs.Features.Events

/// TextWriter wrapper that passes everything through except JasperFx assembly reference noise.
/// JasperFx.RuntimeCompiler walks all loaded assemblies during code gen and Console.WriteLines
/// when it can't resolve transitive deps (e.g. Ionide.ProjInfo → Microsoft.Build.Framework).
type FilteringTextWriter(inner: TextWriter) =
  inherit TextWriter()
  static let isNoise (s: string) =
    not (isNull s)
    && (s.Contains("Could not make an assembly reference to")
        || (s.Contains("System.IO.FileNotFoundException") && s.Contains("Microsoft.Build")))
  override _.Encoding = inner.Encoding
  override _.Write(value: char) = inner.Write(value)
  override _.Write(value: string) = if not (isNoise value) then inner.Write(value)
  override _.WriteLine(value: string) = if not (isNoise value) then inner.WriteLine(value)
  override _.WriteLine() = inner.WriteLine()
  override _.Flush() = inner.Flush()

/// Install the filtering writer once, idempotently
let installFilter =
  lazy (Console.SetOut(new FilteringTextWriter(Console.Out)))

/// Configure a Marten DocumentStore for SageFs event sourcing.
/// Installs a Console.Out filter to suppress JasperFx assembly noise from code gen,
/// and uses Auto code gen mode to minimize unnecessary compilation.
let configureStore (connectionString: string) : IDocumentStore =
  installFilter.Force()
  DocumentStore.For(fun (o: StoreOptions) ->
    o.Connection(connectionString)
    o.Events.StreamIdentity <- JasperFx.Events.StreamIdentity.AsString
    o.AutoCreateSchemaObjects <- JasperFx.AutoCreate.All
    o.GeneratedCodeMode <- JasperFx.CodeGeneration.TypeLoadMode.Auto
    o.UseSystemTextJsonForSerialization(
      configure = fun opts ->
        opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
    )
  )

/// Append events to a session stream with retry on version conflict
let appendEvents (store: IDocumentStore) (streamId: string) (events: SageFsEvent list) =
  let config = RetryPolicy.defaults
  let sw = System.Diagnostics.Stopwatch.StartNew()
  let activity =
    Instrumentation.startSpan Instrumentation.sessionSource "eventstore.append"
      [ ("eventstore.stream_id", box streamId)
        ("eventstore.event_count", box events.Length) ]
  let rec attempt n =
    task {
      try
        use session = store.LightweightSession()
        for evt in events do
          session.Events.Append(streamId, evt :> obj) |> ignore
        do! session.SaveChangesAsync()
        sw.Stop()
        Instrumentation.eventstoreAppendDurationMs.Record(sw.Elapsed.TotalMilliseconds)
        Instrumentation.succeedSpan activity
        return Ok ()
      with ex ->
        match RetryPolicy.decide config n ex with
        | RetryPolicy.RetryAfter delayMs ->
          Instrumentation.eventstoreAppendRetries.Add(1L)
          do! System.Threading.Tasks.Task.Delay(delayMs)
          return! attempt (n + 1)
        | RetryPolicy.GiveUp ex ->
          sw.Stop()
          Instrumentation.eventstoreAppendDurationMs.Record(sw.Elapsed.TotalMilliseconds)
          Instrumentation.eventstoreAppendFailures.Add(1L)
          Instrumentation.failSpan activity ex.Message
          return Error (sprintf "Event append failed after %d attempts: %s" (n + 1) ex.Message)
        | RetryPolicy.Success ->
          Instrumentation.succeedSpan activity
          return Ok ()
    }
  attempt 0

/// Fetch all events from a session stream
let fetchStream (store: IDocumentStore) (streamId: string) =
  task {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let activity =
      Instrumentation.startSpan Instrumentation.sessionSource "eventstore.fetch"
        [ ("eventstore.stream_id", box streamId); ("eventstore.fetch_mode", box "full") ]
    use session = store.LightweightSession()
    let! events = session.Events.FetchStreamAsync(streamId)
    let result =
      events
      |> Seq.choose (fun e ->
        match e.Data with
        | :? SageFsEvent as evt -> Some (e.Timestamp, evt)
        | _ -> None)
      |> Seq.toList
    sw.Stop()
    Instrumentation.eventstoreFetchDurationMs.Record(sw.Elapsed.TotalMilliseconds)
    Instrumentation.eventstoreStreamEventCount.Record(int64 result.Length)
    Instrumentation.succeedSpan activity
    return result
  }

/// Fetch recent events from a session stream (most recent N)
let fetchRecentEvents (store: IDocumentStore) (streamId: string) (count: int) =
  task {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let activity =
      Instrumentation.startSpan Instrumentation.sessionSource "eventstore.fetch"
        [ ("eventstore.stream_id", box streamId)
          ("eventstore.fetch_mode", box "recent")
          ("eventstore.requested_count", box count) ]
    use session = store.LightweightSession()
    let! events = session.Events.FetchStreamAsync(streamId)
    let result =
      events
      |> Seq.choose (fun e ->
        match e.Data with
        | :? SageFsEvent as evt -> Some (e.Timestamp, evt)
        | _ -> None)
      |> Seq.toList
      |> List.rev
      |> List.truncate count
      |> List.rev
    sw.Stop()
    Instrumentation.eventstoreFetchDurationMs.Record(sw.Elapsed.TotalMilliseconds)
    Instrumentation.eventstoreStreamEventCount.Record(int64 result.Length)
    Instrumentation.succeedSpan activity
    return result
  }

/// Count events in a session stream
let countEvents (store: IDocumentStore) (streamId: string) =
  task {
    use session = store.LightweightSession()
    let! events = session.Events.FetchStreamAsync(streamId)
    return events.Count
  }

/// Create a session stream ID
let createSessionId () =
  sprintf "session-%s" (Guid.NewGuid().ToString("N").[..7])

/// Persistence abstraction: PostgreSQL-backed via Marten.
type EventPersistence = {
  AppendEvents: string -> SageFsEvent list -> Threading.Tasks.Task<Result<unit, string>>
  FetchStream: string -> Threading.Tasks.Task<(DateTimeOffset * SageFsEvent) list>
  CountEvents: string -> Threading.Tasks.Task<int>
}

module EventPersistence =
  let postgres (store: IDocumentStore) : EventPersistence = {
    AppendEvents = fun streamId events -> appendEvents store streamId events
    FetchStream = fun streamId -> fetchStream store streamId
    CountEvents = fun streamId -> countEvents store streamId
  }

  /// No-op persistence: silently drops writes, returns empty on reads.
  /// Used when PostgreSQL is unavailable and binary-only mode is active.
  let noop : EventPersistence = {
    AppendEvents = fun _ _ -> Threading.Tasks.Task.FromResult(Ok ())
    FetchStream = fun _ -> Threading.Tasks.Task.FromResult([])
    CountEvents = fun _ -> Threading.Tasks.Task.FromResult(0)
  }

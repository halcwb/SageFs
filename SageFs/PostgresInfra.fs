module SageFs.Server.PostgresInfra

open System
open Testcontainers.PostgreSql

/// Get or auto-start Postgres. Returns Result — failure is fatal.
/// .WithReuse(true) means the container survives SageFs process exit
/// and is reused on next launch (instant restart, no 2s startup).
let getOrStartPostgres () =
  match Environment.GetEnvironmentVariable("SageFs_CONNECTION_STRING") with
  | s when System.String.IsNullOrEmpty s ->
    try
      let container =
        PostgreSqlBuilder()
          .WithDatabase("SageFs")
          .WithUsername("postgres")
          .WithPassword("SageFs")
          .WithImage("postgres:18")
          .WithReuse(true)
          .WithVolumeMount("sagefs-pgdata", "/var/lib/postgresql")
          .Build()
      container.StartAsync().GetAwaiter().GetResult()
      let connStr = container.GetConnectionString()
      Ok connStr
    with ex ->
      Error (sprintf "PostgreSQL is required but unavailable. Install and start Docker, or set SAGEFS_CONNECTION_STRING.\n  Detail: %s" ex.Message)
  | connectionString ->
    Ok connectionString

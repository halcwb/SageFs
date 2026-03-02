module SageFs.Tests.BinaryFormatTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.TestCacheTypes
open SageFs.Features.SessionBinaryTypes

// ─── Helpers ─────────────────────────────────────────────────────

// ─── FsCheck Generators ──────────────────────────────────────────

let genSafeString =
  Gen.elements [""; "hello"; "let x = 42"; "val it : int = 42"; "System.IO"; "/path/to/file.fsx"]

// STC generators
let genOutcome =
  Gen.elements [Outcome.Pass; Outcome.Fail; Outcome.Skip; Outcome.Error]

let genStcTestId =
  Gen.elements ["abc123def456"; ""; "deadbeef0000"; "test_id_here"; "a1b2c3d4e5f6"]

let genCoverageEntry =
  gen {
    let! tid = genStcTestId
    let! wc = Gen.choose(0, 10) |> Gen.map uint32
    let! words = Gen.arrayOfLength (int wc) (Gen.choose(0, Int32.MaxValue) |> Gen.map uint64)
    return { TestId = tid; BitmapWordCount = wc; BitmapWords = words }
  }

let genResultEntry =
  gen {
    let! tid = genStcTestId
    let! outcome = genOutcome
    let! dur = Gen.choose(0, 100000) |> Gen.map uint32
    let! hasMsg = Gen.elements [true; false]
    let! msg =
      match hasMsg with
      | true -> genSafeString |> Gen.map Some
      | false -> Gen.constant None
    return ({ TestId = tid; Outcome = outcome; DurationMs = dur; Message = msg } : ResultEntry)
  }

let genStcData =
  gen {
    let! covCount = Gen.choose(0, 20)
    let! covs = Gen.listOfLength covCount genCoverageEntry
    let! resCount = Gen.choose(0, 20)
    let! results = Gen.listOfLength resCount genResultEntry
    let! gen' = Gen.choose(0, 100) |> Gen.map uint32
    let! ts = Gen.choose(0, 1_000_000_000) |> Gen.map int64
    return { CoverageEntries = covs; ResultEntries = results; ImapGeneration = gen'; CreatedAtMs = ts }
  }

// SFS generators
let genInteractionKind =
  Gen.elements [
    InteractionKind.Interaction; InteractionKind.Expression
    InteractionKind.Directive; InteractionKind.ScriptLoad
  ]

let genEntryFlags =
  Gen.elements [
    EntryFlags.None; EntryFlags.Failed; EntryFlags.HasSideEffects
    EntryFlags.HasOutput; EntryFlags.Failed ||| EntryFlags.HasOutput
  ]

let genRefKind =
  Gen.elements [RefKind.DllPath; RefKind.NuGet; RefKind.IncludePath; RefKind.LoadedScript]

let genInteraction : Gen<Interaction> =
  gen {
    let! code = genSafeString
    let! output = genSafeString
    let! ts = Gen.choose(0, 1_000_000_000) |> Gen.map int64
    let! kind = genInteractionKind
    let! flags = genEntryFlags
    let! dur = Gen.choose(0, 1_000_000) |> Gen.map uint32
    return ({ Code = code; Output = output; TimestampMs = ts; Kind = kind; Flags = flags; DurationMicros = dur } : Interaction)
  }

let genReference : Gen<Reference> =
  gen {
    let! kind = genRefKind
    let! path = genSafeString
    return ({ Kind = kind; Path = path } : Reference)
  }

let genSessionMeta : Gen<SessionMeta> =
  gen {
    let! sv = genSafeString
    let! fv = genSafeString
    let! dv = genSafeString
    let! pp = genSafeString
    let! wd = genSafeString
    let! sid = genSafeString
    let! ec = Gen.choose(0, 10000) |> Gen.map uint32
    let! fc = Gen.choose(0, 100) |> Gen.map uint32
    return {
      SageFsVersion = sv; FSharpVersion = fv; DotNetVersion = dv
      ProjectPath = pp; WorkingDirectory = wd; SessionId = sid
      EvalCount = ec; FailedEvalCount = fc
    }
  }

let genSfsData : Gen<SfsData> =
  gen {
    let! meta = genSessionMeta
    let! intCount = Gen.choose(0, 50)
    let! ints = Gen.listOfLength intCount genInteraction
    let! refCount = Gen.choose(0, 20)
    let! refs = Gen.listOfLength refCount genReference
    let! ts = Gen.choose(0, 1_000_000_000) |> Gen.map int64
    return { Meta = meta; Interactions = ints; References = refs; CreatedAtMs = ts }
  }

// ─── Comparison helpers ──────────────────────────────────────────

let compareCov (a: CoverageEntry) (b: CoverageEntry) =
  a.TestId = b.TestId && a.BitmapWordCount = b.BitmapWordCount && a.BitmapWords = b.BitmapWords

let compareRes (a: ResultEntry) (b: ResultEntry) =
  a.TestId = b.TestId && a.Outcome = b.Outcome && a.DurationMs = b.DurationMs && a.Message = b.Message

let compareStc (a: StcData) (b: StcData) =
  a.ImapGeneration = b.ImapGeneration && a.CreatedAtMs = b.CreatedAtMs &&
  List.length a.CoverageEntries = List.length b.CoverageEntries &&
  List.forall2 compareCov a.CoverageEntries b.CoverageEntries &&
  List.length a.ResultEntries = List.length b.ResultEntries &&
  List.forall2 compareRes a.ResultEntries b.ResultEntries

let compareMeta (a: SessionMeta) (b: SessionMeta) =
  a.SageFsVersion = b.SageFsVersion && a.FSharpVersion = b.FSharpVersion &&
  a.DotNetVersion = b.DotNetVersion && a.ProjectPath = b.ProjectPath &&
  a.WorkingDirectory = b.WorkingDirectory && a.SessionId = b.SessionId &&
  a.EvalCount = b.EvalCount && a.FailedEvalCount = b.FailedEvalCount

let compareInteraction (a: Interaction) (b: Interaction) =
  a.Code = b.Code && a.Output = b.Output && a.TimestampMs = b.TimestampMs &&
  a.Kind = b.Kind && a.Flags = b.Flags && a.DurationMicros = b.DurationMicros

let compareRef (a: Reference) (b: Reference) = a.Kind = b.Kind && a.Path = b.Path

let compareSfs (a: SfsData) (b: SfsData) =
  compareMeta a.Meta b.Meta && a.CreatedAtMs = b.CreatedAtMs &&
  List.length a.Interactions = List.length b.Interactions &&
  List.forall2 compareInteraction a.Interactions b.Interactions &&
  List.length a.References = List.length b.References &&
  List.forall2 compareRef a.References b.References

// ─── Primitive Tests ─────────────────────────────────────────────

let primitiveTests = testList "BinaryPrimitives" [
  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "lp-string roundtrips" <| fun (s: string) ->
      let s = match s with null -> "" | x -> x
      use ms = new IO.MemoryStream()
      use bw = new IO.BinaryWriter(ms)
      BinaryPrimitives.writeLpString bw s
      ms.Position <- 0L
      use br = new IO.BinaryReader(ms)
      BinaryPrimitives.readLpString br = s

  testCase "lp-string empty roundtrips" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw ""
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpString br |> Expect.equal "empty string" ""

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "lp-string-option roundtrips Some" <| fun (s: string) ->
      let s = match s with null -> "" | x -> x
      use ms = new IO.MemoryStream()
      use bw = new IO.BinaryWriter(ms)
      BinaryPrimitives.writeLpStringOption bw (Some s)
      ms.Position <- 0L
      use br = new IO.BinaryReader(ms)
      BinaryPrimitives.readLpStringOption br = Some s

  testCase "lp-string-option None roundtrips" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw None
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br |> Expect.isNone "should be None"

  testCase "lp-string-option Some empty roundtrips" <| fun _ ->
    use ms = new IO.MemoryStream()
    use bw = new IO.BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw (Some "")
    ms.Position <- 0L
    use br = new IO.BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br |> Expect.equal "Some empty" (Some "")

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "CRC-32 deterministic" <| fun (data: byte[]) ->
      let data = match data with null -> [||] | x -> x
      Crc32.computeAll data = Crc32.computeAll data

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "CRC-32 different for different data" <| fun (a: byte[]) (b: byte[]) ->
      let a = match a with null -> [||] | x -> x
      let b = match b with null -> [||] | x -> x
      a = b || Crc32.computeAll a <> Crc32.computeAll b

  testCase "CRC-32 known value" <| fun _ ->
    let data = Text.Encoding.ASCII.GetBytes("123456789")
    Crc32.computeAll data |> Expect.equal "CRC of '123456789'" 0xCBF43926u
]

// ─── STC Tests ───────────────────────────────────────────────────

let stcTests = testList "STC v1" [
  testCase "roundtrip empty data" <| fun _ ->
    let data = StcData.empty
    let bytes = TestCacheWriter.write data
    match TestCacheReader.read bytes with
    | Result.Ok rt -> compareStc data rt |> Expect.isTrue "empty roundtrip"
    | Result.Error e -> failwith e

  testCase "magic bytes correct" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    bytes.[0] |> Expect.equal "S" 0x53uy
    bytes.[1] |> Expect.equal "T" 0x54uy
    bytes.[2] |> Expect.equal "C" 0x43uy
    bytes.[3] |> Expect.equal "1" 0x31uy

  testCase "rejects invalid magic" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    bytes.[0] <- 0xFFuy
    match TestCacheReader.read bytes with
    | Result.Error msg -> msg |> Expect.stringContains "should mention magic" "magic"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "rejects corrupted header CRC" <| fun _ ->
    let bytes = TestCacheWriter.write StcData.empty
    bytes.[40] <- bytes.[40] ^^^ 0xFFuy
    match TestCacheReader.read bytes with
    | Result.Error msg -> msg |> Expect.stringContains "should mention CRC" "CRC"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "rejects too-short data" <| fun _ ->
    match TestCacheReader.read [| 0uy; 1uy; 2uy |] with
    | Result.Error msg -> msg |> Expect.stringContains "should mention short" "short"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "string option None roundtrips in TRES" <| fun _ ->
    let data = {
      StcData.empty with
        ResultEntries = [{ TestId = "test1"; Outcome = Outcome.Pass; DurationMs = 100u; Message = None }]
    }
    let bytes = TestCacheWriter.write data
    match TestCacheReader.read bytes with
    | Result.Ok rt -> (List.head rt.ResultEntries).Message |> Expect.isNone "should be None"
    | Result.Error e -> failwith e

  testCase "string option Some empty roundtrips in TRES" <| fun _ ->
    let data = {
      StcData.empty with
        ResultEntries = [{ TestId = "test2"; Outcome = Outcome.Fail; DurationMs = 50u; Message = Some "" }]
    }
    let bytes = TestCacheWriter.write data
    match TestCacheReader.read bytes with
    | Result.Ok rt -> (List.head rt.ResultEntries).Message |> Expect.equal "should be Some empty" (Some "")
    | Result.Error e -> failwith e

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "full roundtrip random test caches" <|
      Prop.forAll (Arb.fromGen genStcData) (fun data ->
        let bytes = TestCacheWriter.write data
        match TestCacheReader.read bytes with
        | Result.Ok rt -> compareStc data rt
        | Result.Error _ -> false)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
    "header CRC validates" <|
      Prop.forAll (Arb.fromGen genStcData) (fun data ->
        let bytes = TestCacheWriter.write data
        let hdr = Array.zeroCreate<byte> 64
        Array.Copy(bytes, hdr, 64)
        let storedCrc = BitConverter.ToUInt32(hdr, 36)
        hdr.[36] <- 0uy; hdr.[37] <- 0uy; hdr.[38] <- 0uy; hdr.[39] <- 0uy
        Crc32.computeAll hdr = storedCrc)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
    "total_file_size field matches actual" <|
      Prop.forAll (Arb.fromGen genStcData) (fun data ->
        let bytes = TestCacheWriter.write data
        let storedSize = BitConverter.ToUInt64(bytes, 24)
        uint64 bytes.Length = storedSize)
]

// ─── SFS Tests ───────────────────────────────────────────────────

let sfsTests = testList "SFS v3" [
  testCase "roundtrip empty session" <| fun _ ->
    let data = SfsData.empty
    let bytes = SessionBinaryWriter.write data
    match SessionBinaryReader.read bytes with
    | Result.Ok rt -> compareSfs data rt |> Expect.isTrue "empty roundtrip"
    | Result.Error e -> failwith e

  testCase "roundtrip single interaction" <| fun _ ->
    let data = {
      SfsData.empty with
        Meta = { SessionMeta.empty with SageFsVersion = "0.5.403"; EvalCount = 1u }
        Interactions = [
          { Code = "1 + 1"; Output = "val it: int = 2"; TimestampMs = 1000L
            Kind = InteractionKind.Expression; Flags = EntryFlags.HasOutput; DurationMicros = 500u }
        ]
        CreatedAtMs = 42L
    }
    let bytes = SessionBinaryWriter.write data
    match SessionBinaryReader.read bytes with
    | Result.Ok rt -> compareSfs data rt |> Expect.isTrue "single interaction roundtrip"
    | Result.Error e -> failwith e

  testCase "magic bytes are SFS3" <| fun _ ->
    let bytes = SessionBinaryWriter.write SfsData.empty
    bytes.[0] |> Expect.equal "S" 0x53uy
    bytes.[1] |> Expect.equal "F" 0x46uy
    bytes.[2] |> Expect.equal "S" 0x53uy
    bytes.[3] |> Expect.equal "3" 0x33uy

  testCase "rejects invalid magic" <| fun _ ->
    let bytes = SessionBinaryWriter.write SfsData.empty
    bytes.[0] <- 0xFFuy
    match SessionBinaryReader.read bytes with
    | Result.Error msg -> msg |> Expect.stringContains "should mention magic" "magic"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "rejects corrupted header CRC" <| fun _ ->
    let bytes = SessionBinaryWriter.write SfsData.empty
    bytes.[40] <- bytes.[40] ^^^ 0xFFuy
    match SessionBinaryReader.read bytes with
    | Result.Error msg -> msg |> Expect.stringContains "should mention CRC" "CRC"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "rejects corrupted payload" <| fun _ ->
    let data = { SfsData.empty with Meta = { SessionMeta.empty with SageFsVersion = "test" } }
    let bytes = SessionBinaryWriter.write data
    bytes.[64 + 3 * 20 + 2] <- bytes.[64 + 3 * 20 + 2] ^^^ 0xFFuy
    match SessionBinaryReader.read bytes with
    | Result.Error msg -> msg |> Expect.stringContains "should mention CRC" "CRC"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "rejects too-short data" <| fun _ ->
    match SessionBinaryReader.read [| 0uy; 1uy; 2uy |] with
    | Result.Error msg -> msg |> Expect.stringContains "should mention short" "short"
    | Result.Ok _ -> failwith "should have rejected"

  testCase "string pool deduplicates" <| fun _ ->
    let data = {
      SfsData.empty with
        Interactions = [
          { Code = "same"; Output = "same"; TimestampMs = 0L
            Kind = InteractionKind.Interaction; Flags = EntryFlags.None; DurationMicros = 0u }
          { Code = "same"; Output = "same"; TimestampMs = 1L
            Kind = InteractionKind.Interaction; Flags = EntryFlags.None; DurationMicros = 0u }
        ]
    }
    let bytes = SessionBinaryWriter.write data
    match SessionBinaryReader.read bytes with
    | Result.Ok rt -> compareSfs data rt |> Expect.isTrue "dedup roundtrip"
    | Result.Error e -> failwith e

  testCase "references roundtrip" <| fun _ ->
    let data = {
      SfsData.empty with
        References = [
          { Kind = RefKind.DllPath; Path = "/usr/lib/test.dll" }
          { Kind = RefKind.NuGet; Path = "FSharp.Core" }
          { Kind = RefKind.LoadedScript; Path = "script.fsx" }
        ]
    }
    let bytes = SessionBinaryWriter.write data
    match SessionBinaryReader.read bytes with
    | Result.Ok rt -> compareSfs data rt |> Expect.isTrue "refs roundtrip"
    | Result.Error e -> failwith e

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
    "full roundtrip random sessions" <|
      Prop.forAll (Arb.fromGen genSfsData) (fun data ->
        let bytes = SessionBinaryWriter.write data
        match SessionBinaryReader.read bytes with
        | Result.Ok rt -> compareSfs data rt
        | Result.Error _ -> false)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
    "total_file_size matches actual" <|
      Prop.forAll (Arb.fromGen genSfsData) (fun data ->
        let bytes = SessionBinaryWriter.write data
        let storedSize = BitConverter.ToUInt64(bytes, 24)
        uint64 bytes.Length = storedSize)

  testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
    "header CRC validates" <|
      Prop.forAll (Arb.fromGen genSfsData) (fun data ->
        let bytes = SessionBinaryWriter.write data
        let hdr = Array.zeroCreate<byte> 64
        Array.Copy(bytes, hdr, 64)
        let storedCrc = BitConverter.ToUInt32(hdr, 36)
        hdr.[36] <- 0uy; hdr.[37] <- 0uy; hdr.[38] <- 0uy; hdr.[39] <- 0uy
        Crc32.computeAll hdr = storedCrc)
]

// ─── Combined ────────────────────────────────────────────────────

[<Tests>]
let allBinaryFormatTests = testList "Binary Format" [
  primitiveTests
  stcTests
  sfsTests
]

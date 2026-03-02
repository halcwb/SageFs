namespace SageFs.Features

open System
open System.IO
open System.Text
open SageFs

/// Domain types for .sagefs v3 binary format (session persistence).
module SessionBinaryTypes =

  [<RequireQualifiedAccess>]
  type InteractionKind =
    | Interaction = 0us
    | Expression = 1us
    | Directive = 2us
    | ScriptLoad = 3us

  [<System.Flags>]
  type EntryFlags =
    | None = 0us
    | Failed = 1us
    | HasSideEffects = 2us
    | HasOutput = 4us

  [<RequireQualifiedAccess>]
  type RefKind =
    | DllPath = 0uy
    | NuGet = 1uy
    | IncludePath = 2uy
    | LoadedScript = 3uy

  type Interaction = {
    Code: string
    Output: string
    TimestampMs: int64
    Kind: InteractionKind
    Flags: EntryFlags
    DurationMicros: uint32
  }

  type Reference = {
    Kind: RefKind
    Path: string
  }

  type SessionMeta = {
    SageFsVersion: string
    FSharpVersion: string
    DotNetVersion: string
    ProjectPath: string
    WorkingDirectory: string
    EvalCount: uint32
    FailedEvalCount: uint32
    SessionId: string
  }

  type SfsData = {
    Meta: SessionMeta
    Interactions: Interaction list
    References: Reference list
    CreatedAtMs: int64
  }

  module SessionMeta =
    let empty = {
      SageFsVersion = ""; FSharpVersion = ""; DotNetVersion = ""
      ProjectPath = ""; WorkingDirectory = ""
      EvalCount = 0u; FailedEvalCount = 0u; SessionId = ""
    }

  module SfsData =
    let empty = {
      Meta = SessionMeta.empty
      Interactions = []
      References = []
      CreatedAtMs = 0L
    }


/// Writer for .sagefs v3 binary format.
/// Uses string pool architecture for INPT section (deduplicates code/output strings).
module SessionBinaryWriter =
  open SessionBinaryTypes

  let private buildStringPool (strings: string list) : byte[] * Map<string, uint32> =
    let ms = new MemoryStream()
    let mutable offsets = Map.empty<string, uint32>
    for s in strings do
      match Map.tryFind s offsets with
      | Some _ -> ()
      | None ->
        let off = uint32 ms.Position
        let bytes = Encoding.UTF8.GetBytes(s)
        ms.Write(BitConverter.GetBytes(uint32 bytes.Length), 0, 4)
        ms.Write(bytes, 0, bytes.Length)
        offsets <- Map.add s off offsets
    (ms.ToArray(), offsets)

  let private writeMeta (meta: SessionMeta) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw meta.SageFsVersion
    BinaryPrimitives.writeLpString bw meta.FSharpVersion
    BinaryPrimitives.writeLpString bw meta.DotNetVersion
    BinaryPrimitives.writeLpString bw meta.ProjectPath
    BinaryPrimitives.writeLpString bw meta.WorkingDirectory
    BinaryPrimitives.writeLpString bw meta.SessionId
    bw.Write(meta.EvalCount)
    bw.Write(meta.FailedEvalCount)
    bw.Flush()
    ms.ToArray()

  let private writeInpt (interactions: Interaction list) : byte[] =
    let allStrings = interactions |> List.collect (fun i -> [i.Code; i.Output])
    let (poolBytes, poolMap) = buildStringPool allStrings
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write(uint32 (List.length interactions))
    bw.Write(48us) // toc_entry_stride
    for ix in interactions do
      bw.Write(Map.find ix.Code poolMap)
      bw.Write(Map.find ix.Output poolMap)
      bw.Write(ix.TimestampMs)
      bw.Write(uint16 ix.Kind)
      bw.Write(uint16 ix.Flags)
      bw.Write(ix.DurationMicros)
      bw.Write(Array.zeroCreate<byte> 24) // reserved pad to stride=48
    bw.Write(uint32 poolBytes.Length)
    bw.Write(poolBytes)
    bw.Flush()
    ms.ToArray()

  let private writeRefs (refs: Reference list) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write(uint32 (List.length refs))
    for r in refs do
      bw.Write(byte r.Kind)
      BinaryPrimitives.writeLpString bw r.Path
    bw.Flush()
    ms.ToArray()

  let write (data: SfsData) : byte[] =
    let metaP = writeMeta data.Meta
    let inptP = writeInpt data.Interactions
    let refsP = writeRefs data.References

    let headerSize = 64
    let dirSize = 3 * 20
    let metaOff = uint64 (headerSize + dirSize)
    let inptOff = metaOff + uint64 metaP.Length
    let refsOff = inptOff + uint64 inptP.Length
    let totalSize = refsOff + uint64 refsP.Length

    let metaCrc = Crc32.computeAll metaP
    let inptCrc = Crc32.computeAll inptP
    let refsCrc = Crc32.computeAll refsP

    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)

    // Header (64 bytes) — matches spec §2.2
    bw.Write([| 0x53uy; 0x46uy; 0x53uy; 0x33uy |]) // "SFS3"
    bw.Write(3us)                                     // format_version
    bw.Write(3us)                                     // min_reader_version
    bw.Write(3u)                                      // section_count (u32)
    bw.Write(0u)                                      // flags
    bw.Write(data.CreatedAtMs)                        // created_at_ms
    bw.Write(totalSize)                               // total_file_size
    bw.Write(uint32 (List.length data.Interactions))  // interaction_count
    bw.Write(0u)                                      // header_crc32 placeholder
    bw.Write(0u)                                      // string_dedup_count
    bw.Write(0u)                                      // reserved_1
    bw.Write(0UL)                                     // reserved_2
    bw.Write(0UL)                                     // reserved_3

    // Directory: 3 × (tag:u16 + flags:u16 + offset:u64 + size:u32 + crc:u32)
    bw.Write(0x4D45us); bw.Write(0us); bw.Write(metaOff); bw.Write(uint32 metaP.Length); bw.Write(metaCrc)
    bw.Write(0x494Eus); bw.Write(0us); bw.Write(inptOff); bw.Write(uint32 inptP.Length); bw.Write(inptCrc)
    bw.Write(0x5245us); bw.Write(0us); bw.Write(refsOff); bw.Write(uint32 refsP.Length); bw.Write(refsCrc)

    // Payloads
    bw.Write(metaP)
    bw.Write(inptP)
    bw.Write(refsP)
    bw.Flush()

    // Patch header CRC
    let result = ms.ToArray()
    let hdr = Array.zeroCreate<byte> 64
    Array.Copy(result, hdr, 64)
    hdr.[36] <- 0uy; hdr.[37] <- 0uy; hdr.[38] <- 0uy; hdr.[39] <- 0uy
    let hcrc = Crc32.computeAll hdr
    let cb = BitConverter.GetBytes(hcrc)
    Array.Copy(cb, 0, result, 36, 4)
    result


/// Reader for .sagefs v3 binary format.
module SessionBinaryReader =
  open SessionBinaryTypes

  let private ok v : Result<_, string> = Ok v
  let private err msg : Result<_, string> = FSharp.Core.Error msg

  type DirEntry = { Tag: uint16; Flags: uint16; Offset: uint64; Size: uint32; Crc: uint32 }

  let private findSection (tag: uint16) (entries: DirEntry list) =
    entries |> List.tryFind (fun e -> e.Tag = tag)

  let private readPoolString (pool: byte[]) (offset: uint32) : string =
    let off = int offset
    let len = BitConverter.ToUInt32(pool, off) |> int
    Encoding.UTF8.GetString(pool, off + 4, len)

  let private parseMeta (payload: byte[]) : Result<SessionMeta, string> =
    try
      use ms = new MemoryStream(payload)
      use br = new BinaryReader(ms)
      let m = {
        SageFsVersion = BinaryPrimitives.readLpString br
        FSharpVersion = BinaryPrimitives.readLpString br
        DotNetVersion = BinaryPrimitives.readLpString br
        ProjectPath = BinaryPrimitives.readLpString br
        WorkingDirectory = BinaryPrimitives.readLpString br
        SessionId = BinaryPrimitives.readLpString br
        EvalCount = br.ReadUInt32()
        FailedEvalCount = br.ReadUInt32()
      }
      ok m
    with ex -> err (sprintf "META parse error: %s" ex.Message)

  let private parseInpt (payload: byte[]) : Result<Interaction list, string> =
    try
      use ms = new MemoryStream(payload)
      use br = new BinaryReader(ms)
      let count = br.ReadUInt32() |> int
      let stride = br.ReadUInt16() |> int
      let tocStart = ms.Position
      let entries = [
        for i in 0 .. count - 1 do
          ms.Position <- tocStart + int64 (i * stride)
          yield (br.ReadUInt32(), br.ReadUInt32(), br.ReadInt64(), br.ReadUInt16(), br.ReadUInt16(), br.ReadUInt32()) ]
      ms.Position <- tocStart + int64 (count * stride)
      let poolSize = br.ReadUInt32() |> int
      let pool = br.ReadBytes(poolSize)
      ok [
        for (codeOff, outputOff, tsMs, kind, flags, durMicros) in entries do
          yield {
            Code = readPoolString pool codeOff
            Output = readPoolString pool outputOff
            TimestampMs = tsMs
            Kind = LanguagePrimitives.EnumOfValue<uint16, InteractionKind> kind
            Flags = LanguagePrimitives.EnumOfValue<uint16, EntryFlags> flags
            DurationMicros = durMicros
          } ]
    with ex -> err (sprintf "INPT parse error: %s" ex.Message)

  let private parseRefs (payload: byte[]) : Result<Reference list, string> =
    try
      use ms = new MemoryStream(payload)
      use br = new BinaryReader(ms)
      let count = br.ReadUInt32() |> int
      ok [
        for _ in 0 .. count - 1 do
          yield {
            Kind = LanguagePrimitives.EnumOfValue<byte, RefKind>(br.ReadByte())
            Path = BinaryPrimitives.readLpString br
          } ]
    with ex -> err (sprintf "REFS parse error: %s" ex.Message)

  let read (data: byte[]) : Result<SfsData, string> =
    if data.Length < 64 then err "File too short for SFS3 header"
    elif data.[0] <> 0x53uy || data.[1] <> 0x46uy || data.[2] <> 0x53uy || data.[3] <> 0x33uy then
      err "Invalid magic: expected SFS3"
    else
      let storedCrc = BitConverter.ToUInt32(data, 36)
      let hdr = Array.zeroCreate<byte> 64
      Array.Copy(data, hdr, 64)
      hdr.[36] <- 0uy; hdr.[37] <- 0uy; hdr.[38] <- 0uy; hdr.[39] <- 0uy
      if storedCrc <> Crc32.computeAll hdr then
        err (sprintf "Header CRC mismatch: stored=%08X computed=%08X" storedCrc (Crc32.computeAll hdr))
      else
        let sectionCount = BitConverter.ToUInt32(data, 8) |> int
        let createdAtMs = BitConverter.ToInt64(data, 16)
        let dirEntries = [
          for i in 0 .. sectionCount - 1 do
            let o = 64 + i * 20
            yield {
              Tag = BitConverter.ToUInt16(data, o)
              Flags = BitConverter.ToUInt16(data, o + 2)
              Offset = BitConverter.ToUInt64(data, o + 4)
              Size = BitConverter.ToUInt32(data, o + 12)
              Crc = BitConverter.ToUInt32(data, o + 16)
            } ]
        let crcOk = dirEntries |> List.forall (fun e ->
          let p = data.[int e.Offset .. int e.Offset + int e.Size - 1]
          Crc32.computeAll p = e.Crc)
        if not crcOk then err "Section CRC mismatch"
        else
          let getP tag =
            match findSection tag dirEntries with
            | Some e -> ok data.[int e.Offset .. int e.Offset + int e.Size - 1]
            | None -> err (sprintf "Missing section 0x%04X" tag)
          match getP 0x4D45us, getP 0x494Eus, getP 0x5245us with
          | Result.Ok mp, Result.Ok ip, Result.Ok rp ->
            match parseMeta mp, parseInpt ip, parseRefs rp with
            | Result.Ok m, Result.Ok ints, Result.Ok refs ->
              ok { Meta = m; Interactions = ints; References = refs; CreatedAtMs = createdAtMs }
            | Result.Error e, _, _ | _, Result.Error e, _ | _, _, Result.Error e -> err e
          | Result.Error e, _, _ | _, Result.Error e, _ | _, _, Result.Error e -> err e

# SageFs Binary Format Specification

**Version**: 1.0
**Date**: 2026-03-02
**Formats**: `.sagefs` v3 (session persistence), `.sagetc` v1 (test cache)

---

## 1. Overview

SageFs uses two binary file formats for persistence:

| Format | Extension | Magic | Purpose |
|--------|-----------|-------|---------|
| Session | `.sagefs` | `SFS3` | FSI session state: interactions, outputs, assembly blobs, references, profiling |
| Test cache | `.sagetc` | `STC1` | Coverage bitmaps, test outcomes, instrumentation map hash |

Both formats share a common framing: 64-byte header, section directory, and CRC-validated payloads. Magic bytes and section tags distinguish them. All multi-byte integers are **little-endian**.

### 1.1 Design Principles

1. **The persistence format IS the network format.** One serialization path for disk and wire.
2. **Section-based extensibility.** Unknown section tags are skipped by older readers.
3. **CRC integrity at two levels.** Header CRC validates structural metadata; per-section CRCs validate payloads independently.
4. **No trailing sentinel.** File size is in the header. No trailer block.
5. **Conservative encoding.** BARE-inspired: length-prefixed strings, fixed-width integers, no varints except where noted.

### 1.2 Notation

- `u8`, `u16`, `u32`, `u64`: unsigned integers, little-endian
- `i64`: signed 64-bit integer, little-endian
- `lp-string`: length-prefixed UTF-8 string (see §1.3)
- `utf8[N]`: N bytes of raw UTF-8
- All offsets are byte positions from file start unless stated otherwise

### 1.3 String Encoding

**Length-prefixed string (`lp-string`)**:
```
u32     byte_length     (0 is valid — represents an empty string)
u8[N]   utf8_bytes      (N = byte_length, no null terminator)
```

**Optional string (`lp-string-option`)** — used where `string option` semantics are needed:
```
0xFFFFFFFF              → None (no string)
0x00000000              → Some "" (empty string)
0x0000000N (N > 0)      → Some (N-byte UTF-8 string follows)
```

---

## 2. `.sagefs` v3 — Session Persistence Format

### 2.1 File Layout

```
┌──────────────────────────────────────────────────────┐
│  Header (64 bytes)                                    │
├──────────────────────────────────────────────────────┤
│  Section Directory (section_count × 20 bytes)         │
├──────────────────────────────────────────────────────┤
│  Section: META (metadata strings)              [req]  │
├──────────────────────────────────────────────────────┤
│  Section: INPT (interactions + pools)          [req]  │
├──────────────────────────────────────────────────────┤
│  Section: REFS (assembly references)           [req]  │
├──────────────────────────────────────────────────────┤
│  Section: PROF (profiling data)                [opt]  │
├──────────────────────────────────────────────────────┤
│  Section: BIND (runtime bindings)              [opt]  │
├──────────────────────────────────────────────────────┤
│  (future sections skipped by older readers)           │
└──────────────────────────────────────────────────────┘
```

### 2.2 Header (64 bytes)

```
Offset  Size  Type      Field                 Description
──────  ────  ────      ─────                 ───────────
0x00    4     u8[4]     magic                 "SFS3" (0x53, 0x46, 0x53, 0x33)
0x04    2     u16       format_version        = 3
0x06    2     u16       min_reader_version    = 3
0x08    4     u32       section_count         Number of sections in directory
0x0C    4     u32       flags                 Global flags (§2.3)
0x10    8     i64       created_at_ms         Unix epoch milliseconds
0x18    8     u64       total_file_size       Total file size in bytes
0x20    4     u32       interaction_count     Total interactions
0x24    4     u32       header_crc32          CRC-32 of header (§2.10)
0x28    4     u32       string_dedup_count    Unique strings in dedup table (0 if no dedup)
0x2C    4     u32       reserved_1            Must be 0
0x30    8     u64       reserved_2            Must be 0
0x38    8     u64       reserved_3            Must be 0
```

### 2.3 Global Flags

Bit field in `flags` (offset 0x0C):

| Bit | Name | Description |
|-----|------|-------------|
| 0 | `has_assembly_cache` | INPT section contains DLL/PDB blobs |
| 1 | `has_profiling` | PROF section present |
| 2 | `has_bindings` | BIND section present |
| 3 | `section_compression` | Some sections may be zstd-compressed |
| 4 | `has_dedup_table` | INPT string pool uses deduplication |
| 5–31 | reserved | Must be 0 |

### 2.4 Section Directory Entry (20 bytes)

Located immediately after header at offset `0x40`. Total size: `section_count × 20` bytes.

```
Offset  Size  Type      Field       Description
──────  ────  ────      ─────       ───────────
+0x00   2     u16       tag         Section type identifier (§2.5)
+0x02   2     u16       flags       Per-section flags
+0x04   8     u64       offset      Byte offset from file start to payload
+0x0C   4     u32       size        Payload size in bytes
+0x10   4     u32       crc32       CRC-32 of payload bytes
```

**Per-section flags**:

| Bit | Name | Description |
|-----|------|-------------|
| 0 | `is_compressed` | Payload is zstd-compressed; `size` = compressed size |
| 1–15 | reserved | Must be 0 |

When `is_compressed` is set, decompress before CRC validation. The CRC always covers the decompressed payload.

### 2.5 Section Tags

| Tag | Hex | Name | Required | Description |
|-----|-----|------|----------|-------------|
| 1 | 0x0001 | META | Yes | Metadata (versions, paths, session ID) |
| 2 | 0x0002 | INPT | Yes | Interactions (TOC + string pool + blob pool) |
| 3 | 0x0003 | REFS | Yes | Assembly/NuGet references |
| 4 | 0x0004 | PROF | No | Per-interaction profiling/timing |
| 5 | 0x0005 | BIND | No | Runtime binding snapshot |
| 6+ | — | — | No | Reserved for future use |

Readers MUST skip unknown section tags by following the directory entry's `offset` and `size`.

### 2.6 META Section (tag 0x0001)

Flat sequence of fields. No internal header.

```
Field                  Type            Description
─────                  ────            ───────────
sagefs_version         lp-string       e.g. "3.0.0"
fsharp_version         lp-string       e.g. "12.9.100.0"
dotnet_version         lp-string       e.g. "10.0.0"
project_path           lp-string       Absolute path to .fsproj
working_directory      lp-string       Working directory at save time
eval_count             u32             Total successful evaluations
failed_eval_count      u32             Total failed evaluations
session_id             lp-string       UUID of the session
```

### 2.7 INPT Section (tag 0x0002)

**Section header (16 bytes)**:

```
Offset  Size  Type      Field                 Description
──────  ────  ────      ─────                 ───────────
+0x00   4     u32       interaction_count     Redundant with file header (for standalone parsing)
+0x04   4     u32       toc_entry_stride      Bytes per TOC entry (currently 48)
+0x08   4     u32       string_pool_size      String pool size in bytes
+0x0C   4     u32       blob_pool_size        Blob pool size in bytes
```

**TOC entries** (`interaction_count × toc_entry_stride` bytes, immediately after section header):

```
Offset  Size  Type      Field                 Description
──────  ────  ────      ─────                 ───────────
+0x00   4     u32       code_string_offset    Offset into string pool
+0x04   4     u32       code_string_length    Byte length in string pool
+0x08   4     u32       output_string_offset  Offset into string pool
+0x0C   4     u32       output_string_length  0 if no output
+0x10   4     u32       asm_blob_offset       Offset into blob pool
+0x14   4     u32       asm_blob_length       0 if no assembly
+0x18   4     u32       pdb_blob_offset       Offset into blob pool
+0x1C   4     u32       pdb_blob_length       0 if no PDB
+0x20   8     i64       timestamp_ms          Unix epoch milliseconds
+0x28   2     u16       kind                  Interaction kind (§2.7.1)
+0x2A   2     u16       entry_flags           Per-entry flags (§2.7.2)
+0x2C   4     u32       duration_micros       Evaluation duration in microseconds
```

Current stride is 48 bytes. A v4 writer may extend entries to 64 bytes by setting `toc_entry_stride = 64`; v3 readers advance by their known stride (48) and skip trailing bytes.

#### 2.7.1 Interaction Kind

| Value | Name | Example |
|-------|------|---------|
| 0 | `interaction` | `let x = 42;;` |
| 1 | `expression` | `x + 1;;` |
| 2 | `directive` | `#r "nuget: FsCheck"` |
| 3 | `script_load` | `#load "foo.fsx"` |

#### 2.7.2 Entry Flags

| Bit | Name | Description |
|-----|------|-------------|
| 0 | `failed` | Evaluation produced an error |
| 1 | `has_side_effects` | Printed to stdout or mutated state |
| 2 | `has_output` | `output_string_length > 0` |
| 3–15 | reserved | Must be 0 |

**String pool**: Contiguous UTF-8 bytes immediately after all TOC entries. Strings are referenced by `(offset, length)` pairs from TOC entries. No delimiters or null terminators.

**Blob pool**: Raw assembly DLL and PDB bytes immediately after string pool. Referenced by `(offset, length)` pairs.

#### 2.7.3 String Deduplication (Optional)

When `has_dedup_table` flag is set in the file header, repeated strings in the string pool are stored once. Multiple TOC entries reference the same `(offset, length)` pair.

A dedup metadata table is appended after the raw string pool bytes:

```
Field                  Type            Description
─────                  ────            ───────────
magic                  u32             0x44445550 ("DDUP")
unique_count           u32             Number of unique strings
[repeated unique_count times:]
  offset               u32             Offset into string pool
  length               u32             String length in bytes
  ref_count            u32             Number of TOC entries referencing this string
```

### 2.8 REFS Section (tag 0x0003)

```
Field                  Type            Description
─────                  ────            ───────────
ref_count              u32             Number of references
[repeated ref_count times:]
  ref_kind             u8              Reference kind (§2.8.1)
  path                 lp-string       Path or package identifier
```

#### 2.8.1 Reference Kind

| Value | Name | Example |
|-------|------|---------|
| 0 | `dll_path` | `C:\path\to\assembly.dll` |
| 1 | `nuget` | `FSharp.Compiler.Service,41.0.9` |
| 2 | `include_path` | `C:\path\to\include` |
| 3 | `loaded_script` | `C:\path\to\script.fsx` |

### 2.9 PROF Section (tag 0x0004)

```
Field                  Type            Description
─────                  ────            ───────────
entry_count            u32             Number of profiled interactions
[repeated entry_count times:]
  interaction_index    u32             Index into INPT TOC
  parse_micros         u32             Parse phase duration (µs)
  typecheck_micros     u32             Type-check phase duration (µs)
  codegen_micros       u32             Code generation duration (µs)
  emit_micros          u32             IL emit duration (µs)
  load_micros          u32             Assembly load duration (µs)
  total_micros         u32             Wall-clock total (µs)
```

Per-entry size: 28 bytes.

### 2.10 BIND Section (tag 0x0005)

```
Field                  Type            Description
─────                  ────            ───────────
binding_count          u32             Number of bound values
[repeated binding_count times:]
  name                 lp-string       Binding name (e.g. "x")
  type_sig             lp-string       Type signature (e.g. "int")
  value_kind           u8              Value encoding (§2.10.1)
  [if value_kind ∉ {0, 7}:]
    value_length       u32             Byte length of value
    value_bytes        u8[N]           Raw value (little-endian for numeric)
```

#### 2.10.1 Value Kind

| Value | Name | Encoding |
|-------|------|----------|
| 0 | `null` | No value bytes follow |
| 1 | `i32` | 4 bytes, little-endian |
| 2 | `i64` | 8 bytes, little-endian |
| 3 | `f64` | 8 bytes, IEEE 754 |
| 4 | `bool` | 1 byte (0 = false, 1 = true) |
| 5 | `string` | UTF-8 bytes |
| 6 | `bytes` | Raw bytes |
| 7 | `unsupported` | No value bytes follow |

---

## 3. `.sagetc` v1 — Test Cache Format

### 3.1 File Layout

```
┌──────────────────────────────────────────────────────┐
│  Header (64 bytes)                                    │
├──────────────────────────────────────────────────────┤
│  Section Directory (3 × 16 bytes = 48 bytes)          │
├──────────────────────────────────────────────────────┤
│  Section: IMAP (instrumentation map identity)         │
├──────────────────────────────────────────────────────┤
│  Section: TCOV (test coverage bitmaps)                │
├──────────────────────────────────────────────────────┤
│  Section: TRES (test results)                         │
└──────────────────────────────────────────────────────┘
```

### 3.2 Header (64 bytes)

```
Offset  Size  Type      Field                 Description
──────  ────  ────      ─────                 ───────────
0x00    4     u8[4]     magic                 "STC1" (0x53, 0x54, 0x43, 0x31)
0x04    2     u16       format_version        = 1
0x06    2     u16       min_reader_version    = 1
0x08    4     u32       section_count         = 3
0x0C    4     u32       flags                 Reserved, must be 0
0x10    8     i64       created_at_ms         Unix epoch milliseconds
0x18    8     u64       total_file_size       Total file size in bytes
0x20    4     u32       test_count            Total test entries (coverage + results)
0x24    4     u32       header_crc32          CRC-32 of header (§4)
0x28    4     u32       imap_generation       Instrumentation map generation counter
0x2C    20    u8[20]    reserved              Must be zero
```

### 3.3 Section Directory Entry (16 bytes)

Located at offset `0x40`. Fixed count of 3 entries = 48 bytes.

```
Offset  Size  Type      Field       Description
──────  ────  ────      ─────       ───────────
+0x00   4     u32       tag         Section identifier (ASCII, little-endian)
+0x04   8     u64       offset      Byte offset from file start to payload
+0x0C   4     u32       crc32       CRC-32 of payload bytes
```

**Section identifiers** (as u32 little-endian):

| Tag | ASCII | Hex | Description |
|-----|-------|-----|-------------|
| IMAP | "IMAP" | 0x50414D49 | Instrumentation map identity |
| TCOV | "TCOV" | 0x564F4354 | Test coverage bitmaps |
| TRES | "TRES" | 0x53455254 | Test results |

### 3.4 IMAP Section — Instrumentation Map Identity

```
Field                  Type            Description
─────                  ────            ───────────
entry_count            u32             Number of coverage entries
[repeated entry_count times:]
  test_id              u32             Test identifier
  bitmap_word_count    u32             Number of u32 bitmap words
  bitmap_words         u32[N]          Coverage bitmap (N = bitmap_word_count)
```

Each bit in a bitmap word represents one instrumentation probe. Bit 0 of word 0 is probe 0, bit 31 of word 0 is probe 31, bit 0 of word 1 is probe 32, etc.

### 3.5 TCOV Section — Coverage Summary

```
Field                  Type            Description
─────                  ────            ───────────
entry_count            u32             Number of entries
[repeated entry_count times:]
  test_id              u32             Test identifier
  total_probe_bits     u32             Total number of probes (= bitmap_word_count × 32)
```

### 3.6 TRES Section — Test Results

```
Field                  Type            Description
─────                  ────            ───────────
entry_count            u32             Number of result entries
[repeated entry_count times:]
  test_id              u32             Test identifier
  outcome              u8              Test outcome (§3.6.1)
  duration_ms          u32             Test duration in milliseconds
  message              lp-string-option  Result message (§1.3)
```

#### 3.6.1 Test Outcome

| Value | Name |
|-------|------|
| 0 | Pass |
| 1 | Fail |
| 2 | Skip |
| 3 | Error |

---

## 4. CRC-32 Computation

Both formats use CRC-32 (ISO 3309 / ITU-T V.42, polynomial `0xEDB88320` reflected).

### 4.1 Header CRC

1. Write all 64 header bytes to a buffer
2. Zero bytes at offsets `0x24–0x27` (the CRC field itself)
3. Compute `CRC32(buffer[0..63])`
4. Store result at offset `0x24`

Readers validate by copying the header, zeroing `[0x24..0x27]`, computing CRC, and comparing to the stored value.

### 4.2 Section CRC

Each section directory entry contains a `crc32` field covering the section's payload bytes:

```
CRC32(file[offset .. offset + size - 1])
```

For `.sagefs` with compression: decompress first, then CRC the decompressed bytes.

### 4.3 Validation Order

1. Read 64-byte header
2. Validate header CRC
3. Read section directory
4. For each section, validate its CRC before parsing payload

---

## 5. Versioning

### 5.1 Version Fields

- `format_version`: The version of the format that wrote this file
- `min_reader_version`: The minimum reader version required to parse this file

A reader with version `R` can read a file if `R >= min_reader_version`.

### 5.2 Forward Compatibility

**`.sagefs`**: The INPT section's `toc_entry_stride` field enables forward compatibility. A v3 reader encountering a v4 file with 64-byte TOC entries (stride = 64) reads its known 48 bytes per entry and skips the remaining 16.

**Both formats**: Unknown section tags are skipped by following the directory entry's offset and size (`.sagefs`) or offset and next-section offset (`.sagetc`).

### 5.3 Format Differences Summary

| Aspect | `.sagefs` v3 | `.sagetc` v1 |
|--------|-------------|-------------|
| Magic | `SFS3` (0x33534653) | `STC1` (0x31435453) |
| Header size | 64 bytes | 64 bytes |
| Directory entry size | 20 bytes (tag, flags, offset, size, crc) | 16 bytes (tag, offset, crc) |
| Section count | Variable (3–5+) | Fixed: 3 |
| Required sections | META, INPT, REFS | IMAP, TCOV, TRES |
| Optional sections | PROF, BIND | None |
| Compression support | Yes (per-section zstd) | No |
| String deduplication | Optional (DDUP) | N/A |
| Stride extensibility | Yes (toc_entry_stride) | N/A |

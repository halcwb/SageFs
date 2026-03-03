module SageFs.Tests.SharedGenerators

open Expecto
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkerProtocol

/// Shared FsCheck config: 200 random inputs per property
let propConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

/// Lighter config for expensive generators
let lightConfig = { FsCheckConfig.defaultConfig with maxTest = 100 }

// ── Session / Status ──

let genSessionStatus =
  Gen.elements [
    SessionStatus.Starting; SessionStatus.Ready
    SessionStatus.Evaluating; SessionStatus.Faulted
    SessionStatus.Restarting; SessionStatus.Stopped
  ]

// ── Geometry ──

let genSmallRect =
  gen {
    let! w = Gen.choose (1, 40)
    let! h = Gen.choose (1, 30)
    let! row = Gen.choose (0, 50)
    let! col = Gen.choose (0, 80)
    return Rect.create row col w h
  }

let genCellAttrs =
  Gen.elements [
    CellAttrs.None; CellAttrs.Bold; CellAttrs.Dim
    CellAttrs.Inverse
  ]

let genCell =
  gen {
    let! ch = Gen.elements (['A'..'Z'] @ ['a'..'z'] @ [' '; '.'; '|'])
    let! fg = Gen.choose (0, 0xFFFFFF) |> Gen.map uint32
    let! bg = Gen.choose (0, 0xFFFFFF) |> Gen.map uint32
    let! attrs = genCellAttrs
    return Cell.create ch fg bg attrs
  }

// ── Colors ──

let genHexColor =
  gen {
    let! r = Gen.choose (0, 255)
    let! g = Gen.choose (0, 255)
    let! b = Gen.choose (0, 255)
    return sprintf "#%02x%02x%02x" r g b
  }

let genRgbComponents =
  gen {
    let! r = Gen.choose (0, 255) |> Gen.map byte
    let! g = Gen.choose (0, 255) |> Gen.map byte
    let! b = Gen.choose (0, 255) |> Gen.map byte
    return (r, g, b)
  }

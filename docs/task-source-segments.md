# Task: bounded encoder memory — hash table sizing and per-window source segments

## Motivation

A consumer (Charon restore point backups) encodes multi-hundred-MiB TARs against a memory-mapped previous TAR.
The dictionary itself is mapped, but the encoder index grows with it and is larger than it needs to be:

- `BlockHash.CalcTableSize` sizes the bucket table at `dictionaryLength / sizeof(int)` entries rounded up to a power of
  two, i.e. 1–2 × dictionary bytes. Only `dictionaryLength / BlockSize` blocks are ever inserted. A 300 MiB dictionary
  costs a 512 MiB bucket table plus ~75 MiB of chain tables.
- The whole dictionary is always indexed (`EncoderSession.CreateChunkEncoder` builds one `BlockHash` over it), and
  every window references all of it (`WindowEncoder.Output` writes segment `[0, dictionarySize]`).

RFC 3284 §4.2/§4.3 lets each window name its own source segment (length + position). `VcdiffSpanDecoder` already
honours non-zero segment offsets (`_sourceSegmentOffset` / `_sourceSegmentLength`), so only the encoder needs work.

## Part 1 — size the bucket table by block count

- In `BlockHash`, size the bucket table from `blocksCount` (`dictionaryLength / BlockSize`), not from
  `dictionaryLength / sizeof(int)`. Keep it a power of two and keep the "≤ 2 × min" sanity check consistent.
- Check the effect on compression ratio and speed with `VCDiff.Benchmark` against the current sizing (more chain
  collisions are expected; `MAX_PROBES` / `maxMatchesToCheck` bound the probing). Report numbers in the PR.
- No format change: output must still decode with the existing decoder and with xdelta3 (`ExternDiffTests`).

## Part 2 — caller-chosen source segment per window

Add a public way for a `VcdiffSpanEncoder` caller to restrict the source segment of the windows that follow, e.g.:

```csharp
// Ends the window being buffered (encoded against the current segment) and makes later windows
// reference only dictionary bytes [offset, offset + length).
public void SetSourceSegment(long offset, long length);
```

The exact shape is yours to decide, but it must hold to these points:

- Callable between `Encode` calls at any point. Buffered input that does not fill a window is cut into a short window
  and encoded with the previous segment, and its output goes through the usual pending/drain path.
- The index covers only the active segment, so index memory is bounded by the segment length, not the dictionary.
  Rebuild it on a segment change, and reuse the native allocations when the new segment fits.
- COPY addresses are segment-relative. The window header writes the real segment length and position.
- Reject a segment outside the dictionary with `ArgumentOutOfRangeException`. A zero-length segment means "no source"
  for those windows: either omit `VCD_SOURCE` or reject it, and document which.
- Default behaviour (never calling it) is byte-identical to today.
- Getting `VcdiffEncoder` (the stream API) to match is optional. Note in the PR if it is left out.

Out of scope: choosing segments automatically (sliding window, drift tracking). The caller decides.

## Tests

- Round trip with several segment switches (including the switch landing mid-window and segments that overlap or go
  backwards), decoded by `VcdiffSpanDecoder` and, where `ExternDiffTests` can run, by xdelta3.
- A COPY never references bytes outside its window's segment. Assert this by decoding against a dictionary whose bytes
  outside the segment are corrupted.
- Out-of-range segment arguments throw.
- Part 1 has no regression in the existing suites.

## Done when

Both parts are merged with tests, the docs (`docs/streaming-vcdiff-api.md`, README if it lists encoder options) describe
the new API, and the package version is bumped for release.

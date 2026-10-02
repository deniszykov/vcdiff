# Streaming VCDIFF API — Specification

## 1. Purpose

The package exposes a stateful, span-based streaming API for VCDIFF that follows the
zlib-style `OperationStatus` contract. It lets a caller produce or consume a VCDIFF delta
through arbitrary buffers — a `System.IO.Stream`, `System.IO.Pipelines` (`PipeReader` /
`PipeWriter`), a socket, or any other byte transport — rather than being tied to
`System.IO.Stream`.

Two types implement the contract:

| Type | Direction | Method |
|------|-----------|--------|
| `VCDiff.Encoders.VcdiffSpanEncoder` | target bytes → delta bytes | `Encode(...)` |
| `VCDiff.Decoders.VcdiffSpanDecoder` | delta bytes → target bytes | `Decode(...)` |

Both are **stateful and not thread-safe**. Use one instance per logical stream, and dispose
it to unpin the dictionary and release pooled buffers.

## 2. The contract

```csharp
OperationStatus Encode(
    ReadOnlySpan<byte> input,   // source bytes
    Span<byte> output,          // destination bytes
    out int inputConsumed,      // how many input bytes were consumed
    out int outputWritten,      // how many output bytes were written
    bool isFinal);              // true when no more input will follow
```

`Decode` has the identical shape.

`OperationStatus` is `System.Buffers.OperationStatus`.

### 2.1 Status semantics

| Status | Encoder meaning | Decoder meaning |
|--------|-----------------|-----------------|
| `Done` | A **window** (block) was fully emitted — feed more target bytes and call again. With `isFinal`, the whole delta is flushed and the instance is finished. | The whole delta has been decoded and emitted (returned only when `isFinal` is set). |
| `DestinationTooSmall` | Pending delta bytes remain. Re-call with a fresh/larger `output`; the input was already consumed up to `inputConsumed`. | Pending target bytes remain. Re-call with a fresh/larger `output` and the unconsumed rest of `input`. |
| `NeedMoreData` | All `input` was consumed but a full window is not yet accumulated and `isFinal` is false. | All `input` was consumed and more delta bytes are required to finish the current header/window. |
| `InvalidData` | Unused (the encoder has no fatal input error; invalid options throw `ArgumentException` in the constructor). | Bad magic, malformed/truncated delta (when `isFinal`), or a checksum mismatch. |

### 2.2 `inputConsumed` / `outputWritten` semantics

- **Encoder** may consume fewer than `input.Length` bytes in one call when a window becomes
  full; the caller advances by `inputConsumed` and re-calls with the remainder.
- **Decoder** consumes the entire `input` span whenever it returns `NeedMoreData` or `Done`.
  When it returns `DestinationTooSmall` it may have consumed less: it takes input only as fast
  as the output is drained, so the caller advances by `inputConsumed` and re-calls with the
  remainder. It never buffers more than the current window (see [Memory use](#9-memory-use)).
- `outputWritten` is always the number of bytes actually copied into `output`. Both types
  may return a non-zero `outputWritten` together with any of the statuses.

## 3. Constructors and options

```csharp
VcdiffSpanEncoder(ReadOnlySequence<byte> dictionary, VcdiffEncoderOptions? options = null);
VcdiffSpanDecoder(ReadOnlySequence<byte> dictionary, VcdiffDecoderOptions? options = null);
```

The dictionary is `System.Buffers.ReadOnlySequence<byte>`, so it may be backed by a single
array, a `Memory<byte>`, or multiple segments (e.g. the result of
`RecyclableMemoryStream.GetReadOnlySequence()` or a `PipeReader`).

### 3.1 `VcdiffEncoderOptions`

| Property | Default | Meaning |
|----------|---------|---------|
| `MaxWindowSizeMiB` | `1` | Target **window** size in MiB. The encoder is block-oriented and emits one window per `Done`. |
| `BlockSize` | `16` | Block size for hashing; must be even. |
| `HashTableSizeMultiplier` | `0` (open-vcdiff default) | Hash-table buckets per dictionary block. `0` = one bucket per `sizeof(int)` bytes (`BlockSize / 4` per block, matching open-vcdiff, over-allocated to cut collisions); `1` = one bucket per block (smallest table, least memory). |
| `MinMatchSize` | `0` (→ `2 * BlockSize`) | Minimum match length worth a `COPY`; must be ≥ `2 * BlockSize`. |
| `Interleaved` | `false` | Emit the SDCH interleaved format. |
| `WindowChecksumFormat` | `None` | `None`, `Sdch`, or `Xdelta3` window checksum. `Xdelta3` + `Interleaved` throws. |
| `RabinKarpHash` | `null` | Reusable `RabinKarpHash` (caller owns it); otherwise one is created internally. |
| `BytePool` | `ArrayPool<byte>.Shared` | Pool used for internal buffers. |
| `MemoryStreamManager` | library default | `RecyclableMemoryStreamManager` for the pooled streams that hold encoded windows. |

### 3.2 `VcdiffDecoderOptions`

| Property | Default | Meaning |
|----------|---------|---------|
| `MaxTargetWindowSize` | `67108864` (64 MiB) | Maximum target window size in bytes. |
| `DisableChecksums` | `false` | Skip window checksum verification. |
| `BytePool` | `ArrayPool<byte>.Shared` | Pool used for internal buffers. |
| `MemoryStreamManager` | library default | `RecyclableMemoryStreamManager` used by `VcdiffDecoder` to buffer a non-seekable dictionary stream. |

## 4. Dictionary handling

The **dictionary** is the base/“old” file against which the delta is computed (see the
[VCDIFF format notes](#8-format-notes)). It is *referenced by offset*, never embedded in the
delta, so it must be supplied out-of-band to both encoder and decoder.

- It is passed as a `ReadOnlySequence<byte>` and may consist of any number of segments of any
  size. It does **not** have to be one contiguous block of memory.
- It is **not copied**. Each segment is pinned at construction and read in place, so the only
  memory the dictionary costs is the memory the caller already holds.
- Because it is referenced, the memory behind the sequence must stay alive and unchanged until
  the encoder/decoder is disposed. `Dispose` unpins the segments (a finalizer is the safety net).
- Its total length is limited to 2 GiB (VCDIFF addresses are 32-bit in this implementation).
- Lookup is fastest when every segment but the last has the same power-of-two size (as the
  blocks of a `RecyclableMemoryStream` do) or when there is a single segment; other layouts
  fall back to a binary search per access.

### 4.1 Loading a dictionary from a `Stream`

```csharp
using var dictStream = VcdiffDictionary.Read(fileStream); // RecyclableMemoryStream
ReadOnlySequence<byte> dictionary = dictStream.GetReadOnlySequence();

using var encoder = new VcdiffSpanEncoder(dictionary, new VcdiffEncoderOptions { WindowChecksumFormat = WindowChecksumFormat.Sdch });
```

`VcdiffDictionary.Read(Stream, string? tag = null, RecyclableMemoryStreamManager? manager = null)`
reads an entire stream into a pooled `Microsoft.IO.RecyclableMemoryStream` (rented from `manager`, or a
library-wide default), i.e. into many small pooled blocks rather than one large array. The caller owns the returned stream and must dispose it **after** the
encoder/decoder that uses its sequence.

### 4.2 Restricting the source segment per window

`VcdiffSpanEncoder` normally indexes the whole dictionary and gives every window a source segment of
`[0, dictionaryLength)`. A caller that only needs to match against part of the dictionary can restrict it:

```csharp
encoder.SetSourceSegment(offset, length);
```

- Callable between `Encode` calls at any point. Target bytes already buffered for the current window are cut
  into a short window and encoded against the *previous* segment; those delta bytes go through the usual
  pending/drain path, so the caller keeps calling `Encode` to drain them.
- The encoder index is rebuilt to cover only `[offset, offset + length)`, so index memory is bounded by
  `length`, not the dictionary. To keep that bound from the very first window, call `SetSourceSegment` before
  the first `Encode`.
- `COPY` addresses in the windows that follow are segment-relative, and the window header carries the real
  segment length and position (RFC 3284 §4.2). The output still decodes with any compliant decoder.
- `offset` and `length` must be non-negative and `[offset, offset + length)` must lie within the dictionary;
  otherwise `ArgumentOutOfRangeException` is thrown.
- A zero `length` means "no source": those windows are emitted without a source segment (`VCD_SOURCE` is
  omitted).
- Not calling it is byte-identical to the previous behaviour.

## 5. Usage — driving loops

### 5.1 Encoding

Feed target chunks with `isFinal: false`, then issue one final call with an empty input and
`isFinal: true`.

```csharp
static void Encode(VcdiffSpanEncoder encoder, ReadOnlySpan<byte> target, Stream sink, int inChunk)
{
    Span<byte> outBuf = new byte[8192];

    int pos = 0;
    while (pos < target.Length)
    {
        int take = Math.Min(inChunk, target.Length - pos);
        var input = target.Slice(pos, take);

        while (input.Length > 0)
        {
            var status = encoder.Encode(input, outBuf, out int consumed, out int written, isFinal: false);
            sink.Write(outBuf.Slice(0, written));
            input = input.Slice(consumed);
            pos += consumed;

            if (status == OperationStatus.DestinationTooSmall) continue; // outBuf was filled; drain again
            if (status == OperationStatus.NeedMoreData) break;           // input exhausted; feed next chunk
            // Done: window emitted; continue with remaining input in this chunk
        }
    }

    // Final flush of the last (partial) window.
    while (true)
    {
        var status = encoder.Encode(ReadOnlySpan<byte>.Empty, outBuf, out _, out int written, isFinal: true);
        sink.Write(outBuf.Slice(0, written));
        if (status == OperationStatus.Done) break;
    }
}
```

### 5.2 Decoding

Feed delta chunks with `isFinal: false`, then finish with an empty input and `isFinal: true`.

```csharp
static void Decode(VcdiffSpanDecoder decoder, ReadOnlySpan<byte> delta, Stream sink, int inChunk)
{
    Span<byte> outBuf = new byte[8192];

    int pos = 0;
    while (pos < delta.Length)
    {
        int take = Math.Min(inChunk, delta.Length - pos);
        var input = delta.Slice(pos, take);
        pos += take;

        OperationStatus status;
        do
        {
            status = decoder.Decode(input, outBuf, out int consumed, out int written, isFinal: false);
            sink.Write(outBuf.Slice(0, written));
            input = input.Slice(consumed); // may be partial while the output is full
        } while (status == OperationStatus.DestinationTooSmall);

        if (status == OperationStatus.InvalidData) throw new InvalidDataException();
    }

    while (true)
    {
        var status = decoder.Decode(ReadOnlySpan<byte>.Empty, outBuf, out _, out int written, isFinal: true);
        sink.Write(outBuf.Slice(0, written));
        if (status == OperationStatus.Done) break;
        if (status == OperationStatus.InvalidData) throw new InvalidDataException();
    }
}
```

## 6. State and lifecycle

- One instance encodes/decodes exactly one stream; there is no `Reset`.
- After the final `Done`, further calls are safe and return `Done` with zero bytes written.
- `Dispose()` unpins the dictionary and releases pooled internal buffers. It is not
  thread-safe to call `Encode`/`Decode` concurrently.

## 7. Compatibility

- `VcdiffSpanEncoder` produces **byte-identical** output to `VcdiffEncoder` for the same options
  (window boundaries are the same), provided no source segment is set — the stream `VcdiffEncoder`
  has no per-window source-segment API.
- `VcdiffSpanDecoder` decodes every delta the legacy decoder accepts, including external
  xdelta3 (`-S none`) and open-vcdiff/SDCH patches — interleaved, checksummed, app-header,
  and LZMA/XZ secondary-compressed variants.

## 8. Format notes

- VCDIFF is **window-based**; each window is a “block”. The encoder emits one window per
  `Done`, so streaming latency is bounded by `VcdiffEncoderOptions.MaxWindowSizeMiB`.
- `Interleaved` (SDCH) deltas stream their body instruction-by-instruction; non-interleaved
  deltas require the full window body to be buffered before decoding (the decoder does this
  transparently).
- The dictionary is always out-of-band. A delta is meaningless without the exact dictionary
  it was built against; if you need a single self-contained artifact, you must wrap the
  dictionary + delta in your own container.

## 9. Memory use

Nothing is sized by the length of the target or of the delta; only the dictionary index grows
with the dictionary.

| What | Size | Where |
|------|------|-------|
| Dictionary | none (referenced in place) | caller's memory, pinned |
| Encoder dictionary index | by default one hash-table bucket per 4 dictionary bytes (rounded up to a power of two) plus two `int` per block — about 1.5–2.5 × the dictionary length for `BlockSize` 16, matching open-vcdiff; `HashTableSizeMultiplier = 1` shrinks the bucket table to one bucket per block (~0.75–1 ×). Bounded by the active source segment length when `SetSourceSegment` is used | unmanaged, freed on `Dispose` |
| Encoder target window | `MaxWindowSizeMiB` MiB | `BytePool` |
| Encoder window sections and pending output | up to one encoded window | pooled `RecyclableMemoryStream` blocks |
| Decoder input buffer | 16 KiB (grows only for an oversized file header) | `BytePool` |
| Decoder window sections (non-interleaved) | the delta of one window | `BytePool` |
| Decoder target window | the target window length declared by the delta, at most `MaxTargetWindowSize` | `BytePool` |

The decoder rejects a window whose target length or section lengths exceed `MaxTargetWindowSize`,
so a corrupt or hostile delta cannot make it rent an arbitrarily large buffer.

The encoder index is the one allocation proportional to the dictionary. It is required for
matching; if it is too large, raise `BlockSize` (the per-block part shrinks proportionally), set
`HashTableSizeMultiplier` to `1` (one bucket per block instead of the open-vcdiff default of one
per 4 bytes), or restrict the active source segment with `SetSourceSegment` so the index is sized
by the segment instead of the whole dictionary.

## Appendix A — How a segmented dictionary is read

`DictionarySource` keeps a pointer and start offset per pinned segment. The encoder hashes
dictionary blocks in place (a block that straddles two segments is copied to a `BlockSize`
scratch buffer first), and match verification/extension compares one contiguous run at a time
with SIMD, continuing into the neighbouring segment when a run ends at a boundary. The decoder
copies a COPY range segment by segment into the target window. The output is byte-identical
whatever the segmentation.

# vcdiff


[![Nuget](https://img.shields.io/nuget/v/VCdiff)](https://www.nuget.org/packages/VCDiff)
[![GitHub Workflow Status](https://img.shields.io/github/actions/workflow/status/SnowflakePowered/vcdiff/dotnet.yml)](https://github.com/SnowflakePowered/vcdiff/actions?query=workflow%3A.NET)
[![Codecov](https://img.shields.io/codecov/c/github/SnowflakePowered/vcdiff)](https://codecov.io/gh/SnowflakePowered/vcdiff/branch/master)

This is a hard fork of [VCDiff](https://github.com/Metric/VCDiff), originally written by [Metric](https://github.com/Metric), written primarily for use in Snowflake.

Large chunks have been rewritten, and heavily optimized to be *extremely fast*, using vector intrinsics, as well as `Memory<byte>` and `Span<byte>` APIs as well as a sprinkling of unsafe pointer access to eke out every bit of performance possible. Non-scientific preliminary testing shows up to a 30x to 50x speedup compared to the original library when diffing a 2MB file. 

Support for [xdelta3](https://github.com/jmacd/xdelta) checksums have also been included. Testing was done with xdelta 3.1, support for xdelta 3.0 patch files has not been tested. xdelta3 patches without secondary compression (`-S none`) and, for decoding only, with LZMA secondary compression (`-S lzma`) are supported; the `djw` and `fgk` secondary compressors and external compression are not.

|Format|Encoding|Decoding|
|------|--------|--------|
|RFC3284-compliant VCDIFF|✔️|✔️|
|SDHC with Adler32 Checksum|✔️|✔️|
|SDHC Interleaved (with and without Adler32 Checksum)|✔️|✔️|
|xdelta3 with Adler32 Checksum (without compression)|✔️|✔️|
|xdelta3 with Adler32 Checksum and `VCD_APPHEADER` (without compression)|❌|✔️|
|xdelta3 with LZMA secondary compression (`-S lzma`)|❌|✔️|
|xdelta3 with `djw`/`fgk` secondary compression or external compression|❌|❌|

Wherever possible, SSE3 or AVX2 extensions are used on supported systems. Speeds are comparable, albeit slightly slower than the native xdelta3, depending on the chosen blocksize. A lot of work has gone into optimizing out the overhead of garbage collection and memory access through `Memory<T>`, as well as parallelizing computational work with SIMD extensions.

## Why this fork exists

This fork modernizes and de-clutters [SnowflakePowered/vcdiff](https://github.com/SnowflakePowered/vcdiff)
while keeping its performance:

- **Lighter dependencies and smaller build** — the ~2 MiB `SharpCompress` package was replaced with a
  minimal, vendored XZ/LZMA2 decompressor, and unused packages were dropped, shrinking the deployed
  footprint by ~95%.
- **Modern, focused targets** — `netcoreapp3.1` and `net8.0` only, with legacy `netstandard`
  targets and their `#if` fallbacks removed.
- **Modern, allocation-conscious APIs** — configurable buffer pooling
  (`VcEncoderOptions`/`VcDecoderOptions`) and a streaming span-based encode/decode API
  (`VcDiffEncoder`/`VcDiffDecoder`).
- **Same fast paths** — the upstream SIMD/unsafe encode and decode paths are preserved.

The code in this repository was developed and reviewed with the assistance of LLM agents
(DeepSeek and ClaudeCode), under the direction of Denis Zykov.

## Changes in this fork

This fork keeps the fast SIMD/unsafe encode and decode paths from upstream while trimming the dependency surface:

- **Target frameworks**: `netcoreapp3.1` and `net8.0` only (the `netstandard2.0` and `netstandard2.1` targets were removed).
- **Dependencies**: only `Microsoft.IO.RecyclableMemoryStream`. The `SharpCompress` package and its transitive dependencies, as well as `Newtonsoft.Json`, `System.Text.RegularExpressions`, `PolyShim`, and `System.Runtime.CompilerServices.Unsafe`, were removed.
- **Smaller build**: instead of depending on the whole `SharpCompress` package, only a minimal XZ/LZMA2 *decompressor* is vendored into the library (decode-only, LZMA2 filter, using the unsafe/SIMD fast decode loop). This supports the xdelta secondary-compression path and drops all of SharpCompress's archive readers/writers and unrelated compressors.

  | | `VCDiff.dll` netcoreapp3.1 | `VCDiff.dll` net8.0 | total deployed |
  |---|---|---|---|
  | v5.0.0 (NuGet, net6.0/net10.0) | 71 KiB | 62 KiB | ~2.1 MiB (incl. ~2.0 MiB `SharpCompress.dll`) |
  | this fork | 116 KiB | 115 KiB | ~116 KiB |

  `VCDiff.dll` itself grows slightly because it now embeds the LZMA2 decoder, but the fork no longer pulls in SharpCompress's ~2.0 MiB `SharpCompress.dll`, so the deployed footprint drops by roughly 95%.
- **Configurable buffer pooling**: `VcEncoderOptions` and `VcDecoderOptions` accept an `ArrayPool<byte>` (defaulting to `ArrayPool<byte>.Shared` when `null`) that is used for every internal `byte[]`/pinned buffer during encoding and decoding.

### Using the options classes

```csharp
using System.Buffers;
using VCDiff.Decoders;
using VCDiff.Encoders;

var encoderOptions = new VcEncoderOptions
{
    BlockSize = 32,
    BytePool = ArrayPool<byte>.Shared,
};
using var encoder = new VcEncoder(dictStream, targetStream, outputStream, encoderOptions);

var decoderOptions = new VcDecoderOptions
{
    MaxTargetFileSize = 64 * 1024 * 1024,
    BytePool = ArrayPool<byte>.Shared,
};
using var decoder = new VcDecoder(dictStream, deltaStream, outputStream, decoderOptions);
```

## Usage

The examples below diff `fileB.bin` against `fileA.bin` into `diff.bin`, then rebuild
`fileB.bin` from `fileA.bin` + `diff.bin`. `fileA.bin` is the **dictionary** (the old/base
file); the delta references it by offset and never embeds it.

### Legacy `Stream` API

```csharp
using System.IO;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;

// Encode: diff.bin = fileB.bin - fileA.bin
using (var dict   = File.OpenRead("fileA.bin"))
using (var target = File.OpenRead("fileB.bin"))
using (var delta  = File.Create("diff.bin"))
{
    using var encoder = new VcEncoder(dict, target, delta);
    if (encoder.Encode() != VcDiffResult.SUCCESS)
        throw new InvalidOperationException("Encoding failed.");
}

// Decode: fileB.bin = fileA.bin + diff.bin
using (var dict   = File.OpenRead("fileA.bin"))
using (var delta  = File.OpenRead("diff.bin"))
using (var target = File.Create("fileB.decoded.bin"))
{
    using var decoder = new VcDecoder(dict, delta, target);
    if (decoder.Decode(out _) != VcDiffResult.SUCCESS)
        throw new InvalidOperationException("Decoding failed.");
}
```

### Streaming span API

```csharp
using System;
using System.Buffers;
using System.IO;
using VCDiff;
using VCDiff.Decoders;
using VCDiff.Encoders;

// The dictionary is pinned in place (not copied), so it must stay alive and unchanged
// until the encoder/decoder is disposed.

// Encode: diff.bin = fileB.bin - fileA.bin
using (var dictStream = VcDiff.ReadDictionary(File.OpenRead("fileA.bin")))
{
    ReadOnlySequence<byte> dictionary = dictStream.GetReadOnlySequence();
    using var encoder = new VcDiffEncoder(dictionary);
    using var delta = File.Create("diff.bin");
    Encode(encoder, File.OpenRead("fileB.bin"), delta);
}

// Decode: fileB.bin = fileA.bin + diff.bin
using (var dictStream = VcDiff.ReadDictionary(File.OpenRead("fileA.bin")))
{
    ReadOnlySequence<byte> dictionary = dictStream.GetReadOnlySequence();
    using var decoder = new VcDiffDecoder(dictionary);
    using var target = File.Create("fileB.decoded.bin");
    Decode(decoder, File.OpenRead("diff.bin"), target);
}

static void Encode(VcDiffEncoder encoder, Stream source, Stream destination)
{
    var readBuf  = new byte[64 * 1024];
    var writeBuf = new byte[64 * 1024];

    while (true)
    {
        int read = source.Read(readBuf, 0, readBuf.Length);
        if (read == 0) break;

        var input = readBuf.AsSpan(0, read);
        while (input.Length > 0)
        {
            var status = encoder.Encode(input, writeBuf, out int consumed, out int written, isFinal: false);
            destination.Write(writeBuf, 0, written);
            input = input.Slice(consumed);
            if (status == OperationStatus.DestinationTooSmall) continue; // writeBuf full; drain again
            if (status == OperationStatus.NeedMoreData) break;           // input consumed; read next chunk
            // Done: a window was emitted; continue with the remaining input.
        }
    }

    // Flush the final (possibly partial) window.
    while (true)
    {
        var status = encoder.Encode(ReadOnlySpan<byte>.Empty, writeBuf, out _, out int written, isFinal: true);
        destination.Write(writeBuf, 0, written);
        if (status == OperationStatus.Done) break;
    }
}

static void Decode(VcDiffDecoder decoder, Stream source, Stream destination)
{
    var readBuf  = new byte[64 * 1024];
    var writeBuf = new byte[64 * 1024];

    while (true)
    {
        int read = source.Read(readBuf, 0, readBuf.Length);
        if (read == 0) break;

        var input = readBuf.AsSpan(0, read);
        while (input.Length > 0)
        {
            var status = decoder.Decode(input, writeBuf, out int consumed, out int written, isFinal: false);
            destination.Write(writeBuf, 0, written);
            input = input.Slice(consumed);
            if (status == OperationStatus.InvalidData) throw new InvalidDataException("Corrupt delta.");
            // NeedMoreData: all input was consumed (input is now empty).
            // DestinationTooSmall: writeBuf full; continue with the remaining input.
        }
    }

    // Final call marks end of stream and drains any remaining output.
    while (true)
    {
        var status = decoder.Decode(ReadOnlySpan<byte>.Empty, writeBuf, out _, out int written, isFinal: true);
        destination.Write(writeBuf, 0, written);
        if (status == OperationStatus.InvalidData) throw new InvalidDataException("Corrupt delta.");
        if (status == OperationStatus.Done) break;
    }
}
```

<details><summary>The original readme, with some changes to the API usage examples</summary>
<p>

This is a full implementation of open-vcdiff in C# based on [Google's open-vcdiff](https://github.com/google/open-vcdiff). This is written entirely in C# - no external C++ libraries required. This includes proper SDHC support with interleaving and checksums. The only thing it does not support is encoding with a custom CodeTable currently. Will be added later if requested, or feel free to add it in and send a pull request.

It is fully compatible with Google's open-vcdiff for encoding and decoding. If you find any bugs please let me know. I tried to test as thoroughly as possible between this and Google's github version. The largest file I tested with was 10MB. Should be able to support up to 2-4GB depending on your system.

## Requirements
Vector intrinsics and the `Span<T>` and `Memory<T>` memory APIs require .netstandard 2.1.


# Encoding Data
The dictionary must be a file or data that is already in memory. The file must be fully read in first in order to encode properly. This is just how the algorithm works for VCDiff. The encode function is blocking.

```csharp
using VCDiff.Includes;
using VCDiff.Encoders;
using VCDiff.Shared;

void DoEncode() {
    using(FileStream output = new FileStream("...some output path", FileMode.Create, FileAccess.Write))
    using(FileStream dict = new FileStream("..dictionary / old file path", FileMode.Open, FileAccess.Read))
    using(FileStream target = new FileStream("..target data / new data path", FileMode.Open, FileAccess.Read)) {
        VcEncoder coder = new VcEncoder(dict, target, output);
        VcDiffResult result = coder.Encode(); //encodes with no checksum and not interleaved
        if(result != VcDiffResult.SUCCESS) {
            //error was not able to encode properly
        }
    }
}

```

Encoding with checksum or interleaved or both

```csharp
encoder.Encode(interleaved: true, checksumFormat: ChecksumFormat.None);
encoder.Encode(interleaved: true, checksumFormat: ChecksumFormat.SDCH);
encoder.Encode(interleaved: false, checksumFormat: ChecksumFormat.SDCH);
encoder.Encode(interleaved: false, checksumFormat: ChecksumFormat.Xdelta3); // xdelta3 checksums can not be interleaved
```

Modifying the default chunk size for windows

```csharp
int windowSize = 2; //in Megabytes. The default is 1MB window chunks.

VcEncoder coder = new VcEncoder(dict, target, output, windowSize);
```

Modifying the default minimum copy encode size. Which means the match must be >= MinBlockSize in order to qualify as match for copying from dictionary file.

```csharp
// chunkSize is the minimum copy encode size.
// Default is 32 bytes. Lowering this can improve the delta compression for small files. 
// It must be at least twice the block size.
VcEncoder coder = new VcEncoder(dict, target, output, blockSize: 8, chunkSize: 16);
```

Modifying the default BlockSize for hashing

```csharp
// Increasing blockSize for large files with similar data can improve results.
VcEncoder coder = new VcEncoder(dict, target, output, blockSize: 32);
```

# Decoding Data
The dictionary must be a file or data that is already in memory. The file must be fully read in first in order to decode properly. 

Due note the interleaved version of a delta file is meant for streaming and it is supported by the decoder already. However, non-interleaved expects access for reading the full delta file at one time. The delta file is still streamed, but must be able to read fully in sequential order.

```csharp
using VCDiff.Includes;
using VCDiff.Decoders;
using VCDiff.Shared;

void DoDecode() {
    using (FileStream output = new FileStream("...some output path", FileMode.Create, FileAccess.Write))
    using (FileStream dict = new FileStream("..dictionary / old file path", FileMode.Open, FileAccess.Read))
    using (FileStream target = new FileStream("..delta encoded part", FileMode.Open, FileAccess.Read)) {
        VcDecoder decoder = new VcDecoder(dict, target, output);

        // The header of the delta file must be available before the first call to decoder.Decode().
        long bytesWritten = 0;
        VcDiffResult result = decoder.Decode(out bytesWritten);

        if(result != VcDiffResult.SUCCESS) {
            //error decoding
        }

        // if success bytesWritten will contain the number of bytes that were decoded
    }
}
```

`VcDecoder` decodes the whole delta stream in one `Decode` call (a further call returns `VcDiffResult.EOD`). To decode a delta as it arrives, for example an interleaved delta received over the network, use the streaming `VcDiffDecoder` shown above: it accepts the delta in chunks of any size and emits target bytes as soon as they are decoded.

</p>
</details>

# License
vcdiff is a derivative work of [open-vcdiff](https://github.com/google/open-vcdiff) and [xdelta3](https://github.com/jmacd/xdelta), and thus is also licensed under the Apache Public License 2.0.

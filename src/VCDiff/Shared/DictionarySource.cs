using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VCDiff.Shared
{
    /// <summary>
    /// Random access over a dictionary that is stored as one or more memory segments.
    /// The segments are pinned in place and never copied, so the dictionary does not have to be
    /// available as a single contiguous block of memory.
    /// </summary>
    internal sealed unsafe class DictionarySource : IDisposable
    {
        private readonly byte*[] pointers;
        // Start offset of every segment, followed by the total length.
        private readonly long[] starts;
        private readonly MemoryHandle[] handles;
        // When every segment but the last has the same power of two size, the segment index is offset >> shift.
        private readonly int shift;
        private int lastSegment;
        private bool disposed;

        /// <summary>
        /// Pins the segments of <paramref name="sequence"/>. The memory behind the sequence must stay
        /// alive and unchanged until this instance is disposed.
        /// </summary>
        public DictionarySource(ReadOnlySequence<byte> sequence)
        {
            if (sequence.Length > int.MaxValue)
                throw new ArgumentException("The dictionary can not be larger than 2 GiB.", nameof(sequence));

            int count = 0;
            foreach (var memory in sequence)
            {
                if (!memory.IsEmpty)
                    count++;
            }

            pointers = new byte*[count];
            starts = new long[count + 1];
            handles = new MemoryHandle[count];

            int index = 0;
            long offset = 0;
            try
            {
                foreach (var memory in sequence)
                {
                    if (memory.IsEmpty)
                        continue;

                    handles[index] = memory.Pin();
                    pointers[index] = (byte*)handles[index].Pointer;
                    starts[index] = offset;
                    offset += memory.Length;
                    index++;
                }
            }
            catch
            {
                Dispose();
                throw;
            }

            starts[count] = offset;
            shift = CalcShift();
        }

        /// <summary>
        /// Wraps an already pinned (or unmanaged) contiguous block. The block is not owned.
        /// </summary>
        public DictionarySource(byte* pointer, long length)
        {
            if (length > int.MaxValue)
                throw new ArgumentException("The dictionary can not be larger than 2 GiB.", nameof(length));

            if (length <= 0)
            {
                pointers = new byte*[0];
                starts = new long[1];
            }
            else
            {
                pointers = new[] { pointer };
                starts = new[] { 0, length };
            }

            handles = Array.Empty<MemoryHandle>();
            shift = CalcShift();
        }

        ~DictionarySource()
        {
            Dispose();
        }

        public long Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => starts[starts.Length - 1];
        }

        private int CalcShift()
        {
            int count = pointers.Length;
            if (count <= 1)
                return 63;

            long size = starts[1];
            if ((size & (size - 1)) != 0)
                return -1;

            for (int i = 1; i < count - 1; i++)
            {
                if (starts[i + 1] - starts[i] != size)
                    return -1;
            }

            if (starts[count] - starts[count - 1] > size)
                return -1;

            return BitOperations.Log2((ulong)size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int FindSegment(long offset)
        {
            if (shift >= 0)
                return (int)(offset >> shift);

            return FindSegmentSlow(offset);
        }

        private int FindSegmentSlow(long offset)
        {
            int i = lastSegment;
            if (offset >= starts[i] && offset < starts[i + 1])
                return i;

            int lo = 0;
            int hi = pointers.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (starts[mid] <= offset)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            lastSegment = lo;
            return lo;
        }

        /// <summary>
        /// Gets a pointer to the byte at <paramref name="offset"/> and the number of bytes that can
        /// be read from it before the end of the segment. <paramref name="offset"/> must be less
        /// than <see cref="Length"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte* GetPointer(long offset, out long available)
        {
            int i = FindSegment(offset);
            available = starts[i + 1] - offset;
            return pointers[i] + (offset - starts[i]);
        }

        /// <summary>
        /// Gets a pointer one past the byte at <paramref name="offset"/> - 1 and the number of bytes
        /// that can be read before it down to the start of the segment. <paramref name="offset"/>
        /// must be greater than zero.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte* GetPointerBefore(long offset, out long available)
        {
            int i = FindSegment(offset - 1);
            available = offset - starts[i];
            return pointers[i] + available;
        }

        /// <summary>
        /// Copies <c>destination.Length</c> bytes starting at <paramref name="offset"/>.
        /// </summary>
        public void CopyTo(long offset, Span<byte> destination)
        {
            while (destination.Length > 0)
            {
                byte* p = GetPointer(offset, out long available);
                int take = (int)Math.Min(available, destination.Length);
                new ReadOnlySpan<byte>(p, take).CopyTo(destination);
                destination = destination.Slice(take);
                offset += take;
            }
        }

        /// <summary>
        /// Whether the <paramref name="length"/> bytes at <paramref name="offset"/> equal the bytes at <paramref name="other"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SequenceEqual(long offset, byte* other, int length)
        {
            byte* p = GetPointer(offset, out long available);
            if (available >= length)
                return new ReadOnlySpan<byte>(p, length).SequenceEqual(new ReadOnlySpan<byte>(other, length));

            return MatchForward(offset, other, length) == length;
        }

        /// <summary>
        /// Counts how many bytes starting at <paramref name="offset"/> equal the bytes starting at
        /// <paramref name="other"/>, up to <paramref name="maxBytes"/>.
        /// </summary>
        public long MatchForward(long offset, byte* other, long maxBytes)
        {
            long found = 0;
            while (found < maxBytes)
            {
                byte* p = GetPointer(offset + found, out long available);
                long take = Math.Min(available, maxBytes - found);
                long matched = CommonPrefixLength(p, other + found, take);
                found += matched;
                if (matched < take)
                    break;
            }

            return found;
        }

        /// <summary>
        /// Counts how many bytes before <paramref name="offset"/> equal the bytes before
        /// <paramref name="otherEnd"/>, up to <paramref name="maxBytes"/>.
        /// </summary>
        public long MatchBackward(long offset, byte* otherEnd, long maxBytes)
        {
            long found = 0;
            while (found < maxBytes)
            {
                byte* p = GetPointerBefore(offset - found, out long available);
                long take = Math.Min(available, maxBytes - found);
                long matched = CommonSuffixLength(p, otherEnd - found, take);
                found += matched;
                if (matched < take)
                    break;
            }

            return found;
        }

        private static long CommonPrefixLength(byte* a, byte* b, long length)
        {
            long i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                int vectorSize = Vector<byte>.Count;
                while (i <= length - vectorSize)
                {
                    if (Unsafe.ReadUnaligned<Vector<byte>>(a + i) != Unsafe.ReadUnaligned<Vector<byte>>(b + i))
                        break;

                    i += vectorSize;
                }
            }

            while (i <= length - sizeof(ulong) && Unsafe.ReadUnaligned<ulong>(a + i) == Unsafe.ReadUnaligned<ulong>(b + i))
                i += sizeof(ulong);

            while (i < length && a[i] == b[i])
                i++;

            return i;
        }

        private static long CommonSuffixLength(byte* aEnd, byte* bEnd, long length)
        {
            long i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                int vectorSize = Vector<byte>.Count;
                while (i <= length - vectorSize)
                {
                    if (Unsafe.ReadUnaligned<Vector<byte>>(aEnd - i - vectorSize) != Unsafe.ReadUnaligned<Vector<byte>>(bEnd - i - vectorSize))
                        break;

                    i += vectorSize;
                }
            }

            while (i <= length - sizeof(ulong) && Unsafe.ReadUnaligned<ulong>(aEnd - i - sizeof(ulong)) == Unsafe.ReadUnaligned<ulong>(bEnd - i - sizeof(ulong)))
                i += sizeof(ulong);

            while (i < length && aEnd[-i - 1] == bEnd[-i - 1])
                i++;

            return i;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            for (int i = 0; i < handles.Length; i++)
                handles[i].Dispose();

            GC.SuppressFinalize(this);
        }
    }
}

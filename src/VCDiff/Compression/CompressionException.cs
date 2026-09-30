// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;

namespace VCDiff.Compression
{
    /// <summary>
    /// Base exception for errors thrown by the vendored XZ/LZMA decompression code.
    /// </summary>
    public class CompressionException : Exception
    {
        public CompressionException() { }

        public CompressionException(string message)
            : base(message) { }

        public CompressionException(string message, Exception inner)
            : base(message, inner) { }
    }

    public class IncompleteArchiveException : CompressionException
    {
        public IncompleteArchiveException(string message)
            : base(message) { }
    }

    public class InvalidFormatException : CompressionException
    {
        public InvalidFormatException() { }

        public InvalidFormatException(string message)
            : base(message) { }

        public InvalidFormatException(string message, Exception inner)
            : base(message, inner) { }
    }
}

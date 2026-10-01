namespace VCDiff;

/// <summary>
///     The category of a <see cref="VcdiffException" />, allowing callers to distinguish failure kinds without
///     catching multiple exception types.
/// </summary>
public enum VcdiffError
{
	/// <summary>The input ended before the expected amount of data was available.</summary>
	Truncated,

	/// <summary>Data was present but malformed, corrupt, or self-inconsistent.</summary>
	InvalidFormat,

	/// <summary>A caller-supplied argument was invalid for the requested operation.</summary>
	InvalidArgument,

	/// <summary>The input is valid, but the feature or configuration is not supported.</summary>
	Unsupported,

	/// <summary>An I/O operation on a stream failed.</summary>
	IoError,

	/// <summary>An internal invariant was violated; this indicates a bug.</summary>
	InternalError,
}
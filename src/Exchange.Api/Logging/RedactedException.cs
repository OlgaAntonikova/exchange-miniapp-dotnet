namespace Exchange.Api.Logging;

/// <summary>
/// Stands in for a logged exception: its message and its full text (type, message, stack, inner
/// exceptions) with secret patterns masked. Formatters print an exception through
/// <see cref="ToString"/>, so this is all a sink ever sees.
/// </summary>
public sealed class RedactedException : Exception
{
    private readonly string _text;

    /// <summary>Masks <paramref name="original"/>.</summary>
    public RedactedException(Exception original)
        : base(LogRedactor.MaskText(original?.Message ?? string.Empty)) =>
        _text = LogRedactor.MaskText(original?.ToString() ?? string.Empty);

    /// <summary>Required by the exception pattern; prefer the wrapping constructor.</summary>
    public RedactedException()
        : this(new InvalidOperationException())
    {
    }

    /// <summary>Required by the exception pattern; the message is masked.</summary>
    public RedactedException(string message)
        : this(new InvalidOperationException(message))
    {
    }

    /// <summary>Required by the exception pattern; the message and the inner exception are masked.</summary>
    public RedactedException(string message, Exception innerException)
        : this(new InvalidOperationException(message, innerException))
    {
    }

    /// <inheritdoc />
    public override string ToString() => _text;
}

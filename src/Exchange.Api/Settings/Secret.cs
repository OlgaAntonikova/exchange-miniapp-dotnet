namespace Exchange.Api.Settings;

/// <summary>
/// A secret value (RULE-SECRETS). Prints as <c>**********</c> wherever it is formatted - logs,
/// exceptions, string interpolation - and exposes its value only through <see cref="Reveal"/>.
/// It has no public property, so a serializer finds nothing to write.
/// </summary>
public sealed class Secret
{
    /// <summary>What every secret prints as.</summary>
    public const string Mask = "**********";

    private readonly string _value;

    /// <summary>Wraps a secret value.</summary>
    public Secret(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    /// <summary>The secret itself, for the one call that needs it.</summary>
    public string Reveal() => _value;

    /// <inheritdoc />
    public override string ToString() => Mask;
}

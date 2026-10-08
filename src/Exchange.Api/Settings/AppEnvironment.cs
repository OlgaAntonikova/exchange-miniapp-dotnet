namespace Exchange.Api.Settings;

/// <summary>The value of <c>APP_ENV</c> (spec 1.11).</summary>
public enum AppEnvironment
{
    /// <summary>A developer's machine; reads <c>.env</c>.</summary>
    Development,

    /// <summary>The test suite.</summary>
    Test,

    /// <summary>A real installation.</summary>
    Production,

    /// <summary>The self-contained demo of spec 1.9: no secret, no Telegram.</summary>
    Demo,
}

/// <summary>Output format of the logs (spec 4).</summary>
public enum LogFormat
{
    /// <summary>A plain console template.</summary>
    Console,

    /// <summary>JSON lines.</summary>
    Json,
}

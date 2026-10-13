using Exchange.Api.Logging;
using Serilog;
using Serilog.Core;
using Shouldly;

namespace Exchange.Api.Tests.Logging;

/// <summary>D-044: data protection's warnings are dropped, its errors kept; the rest of ASP.NET Core still warns.</summary>
public sealed class DataProtectionLogTests
{
    [Fact]
    public void Data_protection_logs_only_errors_while_aspnetcore_keeps_its_warnings()
    {
        var sink = new CapturingSink();
        using (var logger = LoggingSetup.Configure(new LoggerConfiguration(), "Debug", sink, alerts: null).CreateLogger())
        {
            var keys = logger.ForContext(Constants.SourceContextPropertyName, LoggingSetup.DataProtectionCategory + ".KeyManagement.XmlKeyManager");
            keys.Warning("No XML encryptor configured");
            keys.Error("Key ring unreadable");
            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Server.Kestrel").Warning("Overriding addresses");
        }

        sink.Events.Select(e => e.MessageTemplate.Text).ShouldBe(["Key ring unreadable", "Overriding addresses"]);
    }
}

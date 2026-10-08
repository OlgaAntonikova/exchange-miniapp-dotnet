using Exchange.Api.Data;
using Exchange.Domain.Directions;
using Exchange.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Exchange.Api.Reference;

/// <summary>The public reference endpoints of spec 1.10: active rows only, ordered by id.</summary>
public static class ReferenceEndpoints
{
    /// <summary>A city, as <c>GET /api/cities</c> lists it.</summary>
    /// <param name="Id">Identity.</param>
    /// <param name="Name">The city's name.</param>
    /// <param name="MarkupPercent">Markup in percent, as a string at scale 4.</param>
    public sealed record CityOut(int Id, string Name, decimal MarkupPercent);

    /// <summary>A bank, as <c>GET /api/banks</c> lists it.</summary>
    /// <param name="Id">Identity.</param>
    /// <param name="Code">The bank's code.</param>
    /// <param name="Channel"><c>WIRE</c> or <c>ATM</c>.</param>
    /// <param name="Currency">ISO 4217 code.</param>
    /// <param name="Country">ISO 3166-1 alpha-2 code.</param>
    /// <param name="MarkupPercent">Markup in percent.</param>
    public sealed record BankOut(int Id, string Code, string Channel, string Currency, string Country, decimal MarkupPercent);

    /// <summary>A USDT network, as <c>GET /api/usdt-networks</c> lists it.</summary>
    /// <param name="Id">Identity.</param>
    /// <param name="Code">The network's code.</param>
    /// <param name="FeePercent">Fee in percent.</param>
    public sealed record UsdtNetworkOut(int Id, string Code, decimal FeePercent);

    /// <summary>A direction, as <c>GET /api/directions</c> lists it.</summary>
    /// <param name="Give">The give code.</param>
    /// <param name="Receive">The receive code.</param>
    /// <param name="QuoteFields">What a quote needs, sorted.</param>
    /// <param name="OrderFields">What an order needs, sorted.</param>
    /// <param name="OptionalFields">What an order may add, sorted.</param>
    public sealed record DirectionOut(string Give, string Receive, IReadOnlyList<string> QuoteFields, IReadOnlyList<string> OrderFields, IReadOnlyList<string> OptionalFields);

    /// <summary>Maps the four reference endpoints under <c>/api</c>.</summary>
    public static IEndpointRouteBuilder MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/cities", async (ExchangeDbContext db, CancellationToken cancellationToken) =>
            await db.Cities.AsNoTracking().Where(city => city.IsActive).OrderBy(city => city.Id)
                .Select(city => new CityOut(city.Id, city.Name, city.MarkupPercent))
                .ToListAsync(cancellationToken).ConfigureAwait(false));

        api.MapGet("/banks", async (ExchangeDbContext db, CancellationToken cancellationToken) =>
            (await db.Banks.AsNoTracking().Where(bank => bank.IsActive).OrderBy(bank => bank.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(bank => new BankOut(bank.Id, bank.Code, OrderCodes.Of(bank.Channel), bank.Currency, bank.Country, bank.MarkupPercent))
                .ToList());

        api.MapGet("/usdt-networks", async (ExchangeDbContext db, CancellationToken cancellationToken) =>
            await db.UsdtNetworks.AsNoTracking().Where(network => network.IsActive).OrderBy(network => network.Id)
                .Select(network => new UsdtNetworkOut(network.Id, network.Code, network.FeePercent))
                .ToListAsync(cancellationToken).ConfigureAwait(false));

        // The same table the API enforces, so the form never keeps a copy that could drift (D-026).
        var directions = Directions.All.Select(direction =>
        {
            var quote = Directions.FieldsFor(direction.Give, direction.Receive, Purpose.Quote);
            var order = Directions.FieldsFor(direction.Give, direction.Receive, Purpose.Order);
            return new DirectionOut(
                Codes.Of(direction.Give),
                Codes.Of(direction.Receive),
                [.. quote.Required.Order(StringComparer.Ordinal)],
                [.. order.Required.Order(StringComparer.Ordinal)],
                [.. order.Optional.Order(StringComparer.Ordinal)]);
        }).ToList();
        api.MapGet("/directions", () => directions);

        return app;
    }
}

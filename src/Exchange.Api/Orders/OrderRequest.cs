using System.Text.Json;
using Exchange.Api.Pricing;
using Exchange.Domain.Directions;
using Exchange.Domain.Orders;

namespace Exchange.Api.Orders;

/// <summary>The client-typed details of an order, normalized by the rules of spec 1.1; null when absent.</summary>
/// <param name="UsdtTransferTime">When USDT is sent.</param>
/// <param name="Iban">The IBAN, without spaces, upper-cased.</param>
/// <param name="RecipientName">The recipient's name.</param>
/// <param name="WalletAddress">The wallet address.</param>
/// <param name="PaymentPurpose">The payment purpose.</param>
public sealed record OrderDetails(UsdtTransferTime? UsdtTransferTime, string? Iban, string? RecipientName, string? WalletAddress, string? PaymentPurpose);

/// <summary>An order request: its priced part, its details, and the extra fields it carries.</summary>
/// <param name="Pricing">The direction, the amount and the reference ids.</param>
/// <param name="Details">The client-typed details.</param>
/// <param name="Provided">The names of every extra field present, for the direction check.</param>
public sealed record OrderInput(PricingInput Pricing, OrderDetails Details, IReadOnlyList<string> Provided);

/// <summary>
/// The request rules of spec 1.10 for <c>POST /api/orders</c>: the quote's rules, plus the detail
/// fields, each checked by its rule of spec 1.1. Every bad field is listed at once, the priced ones
/// first; a detail sent as JSON <c>null</c> or as an empty string counts as absent (D-038).
/// </summary>
public static class OrderRequest
{
    /// <summary>The order's parts of <paramref name="root"/>.</summary>
    /// <exception cref="PricingException"><c>invalid_request</c>, listing every field with a bad value.</exception>
    public static OrderInput Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PricingException(PricingRequest.InvalidRequest, [PricingRequest.WholeBody]);
        }

        var bad = new List<string>();
        PricingInput? pricing = null;
        try
        {
            pricing = PricingRequest.Parse(root);
        }
        catch (PricingException refused)
        {
            bad.AddRange(refused.Fields);
        }

        var transferTime = Detail(root, DetailFields.UsdtTransferTime, bad, text => Codes.TryParseTransferTime(text, out var time) ? time : (UsdtTransferTime?)null);
        var iban = Detail(root, DetailFields.Iban, bad, text => DetailFieldRules.TryIban(text, out var value) ? value : null);
        var recipient = Detail(root, DetailFields.RecipientName, bad, text => DetailFieldRules.TryRecipientName(text, out var value) ? value : null);
        var wallet = Detail(root, DetailFields.WalletAddress, bad, text => DetailFieldRules.TryWalletAddress(text, out var value) ? value : null);
        var purpose = Detail(root, DetailFields.PaymentPurpose, bad, text => DetailFieldRules.TryPaymentPurpose(text, out var value) ? value : null);
        if (bad.Count > 0)
        {
            throw new PricingException(PricingRequest.InvalidRequest, bad);
        }

        var details = new OrderDetails(transferTime, iban, recipient, wallet, purpose);
        var provided = pricing!.ReferenceIds.Keys
            .Concat(new (string Name, bool Present)[]
            {
                (DetailFields.UsdtTransferTime, transferTime is not null),
                (DetailFields.Iban, iban is not null),
                (DetailFields.RecipientName, recipient is not null),
                (DetailFields.WalletAddress, wallet is not null),
                (DetailFields.PaymentPurpose, purpose is not null),
            }.Where(field => field.Present).Select(field => field.Name))
            .ToList();
        return new OrderInput(pricing, details, provided);
    }

    // A detail is absent when the property is missing, null or an empty string; otherwise it must be
    // a string its rule accepts. A refusal names the field only (RULE-SECRETS).
    private static T? Detail<T>(JsonElement root, string field, List<string> bad, Func<string, T?> accept)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null
            || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0))
        {
            return default;
        }

        if (value.ValueKind == JsonValueKind.String && accept(value.GetString()!) is { } accepted)
        {
            return accepted;
        }

        bad.Add(PricingRequest.FieldPrefix + field);
        return default;
    }
}

using System.Text.Json;
using DataBridge.DTOs;
using DataBridge.Models;

namespace DataBridge.Services;

// The model's reply is untrusted input. Everything here is checked in C#, whatever
// the schema sent to OpenAI asked for, and any problem rejects the whole reply.
public static class CustomerAiFilterParser
{
    private static readonly string[] RequiredFields = { "supported", "location", "status", "company", "name" };

    // Returns null when the model says the question isn't a customer search.
    public static CustomerSearchFilters? Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw Invalid();
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }

            var fieldNames = root.EnumerateObject().Select(field => field.Name).ToList();
            if (fieldNames.Count != RequiredFields.Length || RequiredFields.Any(field => !fieldNames.Contains(field)))
            {
                throw Invalid();
            }

            JsonElement supported = root.GetProperty("supported");
            if (supported.ValueKind != JsonValueKind.True && supported.ValueKind != JsonValueKind.False)
            {
                throw Invalid();
            }

            var filters = new CustomerSearchFilters
            {
                Location = ReadText(root, "location", Customer.LocationMaxLength),
                Status = ReadStatus(root),
                Company = ReadText(root, "company", Customer.CompanyMaxLength),
                Name = ReadText(root, "name", Customer.NameMaxLength)
            };

            return supported.ValueKind == JsonValueKind.True ? filters : null;
        }
    }

    private static string? ReadText(JsonElement root, string fieldName, int maxLength)
    {
        JsonElement field = root.GetProperty(fieldName);

        if (field.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (field.ValueKind != JsonValueKind.String)
        {
            throw Invalid();
        }

        string text = field.GetString()!.Trim();

        if (text.Length > maxLength || text.Any(char.IsControl))
        {
            throw Invalid();
        }

        return text.Length == 0 ? null : text;
    }

    private static CustomerStatus? ReadStatus(JsonElement root)
    {
        JsonElement field = root.GetProperty("status");

        if (field.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return field.ValueKind == JsonValueKind.String ? field.GetString() switch
        {
            nameof(CustomerStatus.Active) => CustomerStatus.Active,
            nameof(CustomerStatus.Inactive) => CustomerStatus.Inactive,
            _ => throw Invalid()
        } : throw Invalid();
    }

    private static AiSearchException Invalid()
    {
        return new AiSearchException(AiSearchErrorType.InvalidOutput,
            "AI search returned an answer this app can't use, so no search ran. Try rewording, or use the location and status filters.");
    }
}

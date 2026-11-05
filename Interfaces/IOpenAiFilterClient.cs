namespace DataBridge.Interfaces;

public interface IOpenAiFilterClient
{
    // Returns the model's JSON text, or null if the model refused to answer.
    // Throws AiSearchException if there is no usable reply.
    Task<string?> RequestFiltersJsonAsync(string question);
}

namespace DataBridge.Services;

// Thrown by DataBridgeApiClient, so a page can show Message and check StatusCode.
public class ApiException : Exception
{
    // Zero means no response arrived from the API.
    public int StatusCode { get; }

    public ApiException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

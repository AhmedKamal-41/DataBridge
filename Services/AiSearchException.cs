namespace DataBridge.Services;

public enum AiSearchErrorType
{
    NotConfigured,
    RequestFailed,
    InvalidOutput
}

public class AiSearchException : Exception
{
    public AiSearchErrorType ErrorType { get; }

    public AiSearchException(AiSearchErrorType errorType, string message)
        : base(message)
    {
        ErrorType = errorType;
    }
}

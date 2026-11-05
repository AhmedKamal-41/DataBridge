namespace DataBridge.Services;

public enum ImportErrorType
{
    Invalid,
    TooLarge,
    Unsupported,
    Locked
}

// Thrown when an import breaks a business rule. The service does not know about
// HTTP, so the controller turns the error type into a status code.
public class ImportException : Exception
{
    public ImportErrorType ErrorType { get; }

    public ImportException(ImportErrorType errorType, string message)
        : base(message)
    {
        ErrorType = errorType;
    }
}

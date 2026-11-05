namespace DataBridge.Models;

public enum ImportJobStage
{
    Queued,
    Parsing,
    Saving,
    Validating,
    Completed,
    Failed
}

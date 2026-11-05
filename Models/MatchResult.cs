namespace DataBridge.Models;

// What the system found when it compared a row with existing customers.
public enum MatchResult
{
    New,
    Duplicate,
    Conflict
}

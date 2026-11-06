namespace DataBridge.Interfaces;

public interface IAiUsageCap
{
    // Reserves one paid AI call in today's shared total. Returns false at the cap.
    Task<bool> TryReserveCallAsync();
}

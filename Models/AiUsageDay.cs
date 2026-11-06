namespace DataBridge.Models;

// How many paid AI calls every workspace has made together on one UTC day.
public class AiUsageDay
{
    public DateOnly Day { get; set; }
    public int Calls { get; set; }
}

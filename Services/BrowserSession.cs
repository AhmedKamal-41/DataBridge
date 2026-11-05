namespace DataBridge.Services;

// What DataBridgeApiClient needs to call the API as the signed-in browser. The pages
// run on the server, so their API calls don't carry the browser's cookies by themselves.
// App.razor reads these from the page request, and each circuit gets its own copy.
public class BrowserSession
{
    public string? CookieHeader { get; set; }
    public string? AntiforgeryToken { get; set; }
}

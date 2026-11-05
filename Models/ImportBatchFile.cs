namespace DataBridge.Models;

// The uploaded bytes. Kept apart from ImportBatch so that normal batch queries
// don't load the file, and saved with the batch so a job can always replay it.
public class ImportBatchFile
{
    public int ImportBatchId { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

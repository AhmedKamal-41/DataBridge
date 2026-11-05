using System.ComponentModel.DataAnnotations;

namespace DataBridge.DTOs;

// Only the controller uses this class. It passes the filename and a stream
// to the service, so IFormFile never reaches the service.
public class UploadImportRequest
{
    [Required]
    public IFormFile? File { get; set; }
}

using Help_Desk.Models;

namespace Help_Desk.Services;

public static class TicketPhotoUpload
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static async Task<(TicketPhoto? Photo, string? Error)> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0) return (null, "Choose a non-empty photo, or leave the optional photo blank.");
        if (file.Length > MaxBytes) return (null, "The photo must be 5 MB or smaller.");
        using var memory = new MemoryStream((int)file.Length);
        await file.CopyToAsync(memory, cancellationToken);
        var data = memory.ToArray();
        string? contentType = null;
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) contentType = "image/png";
        else if (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255) contentType = "image/jpeg";
        else if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) contentType = "image/webp";
        if (contentType is null) return (null, "Upload a PNG, JPG, or WebP photo.");
        var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
        name = string.Concat(name.Where(c => !char.IsControl(c)));
        if (string.IsNullOrWhiteSpace(name)) name = "Ticket photo";
        if (name.Length > 180) name = name[..180];
        return (new TicketPhoto { Data = data, ContentType = contentType, FileName = name }, null);
    }
}

namespace Help_Desk.Services;

public class TicketEmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "AITS Helpdesk";
    public string? Password { get; set; }
    public string PublicBaseUrl { get; set; } = "";
    public string Transport { get; set; } = "Smtp";
    public int PollSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;
}

public enum Stage
{
    EnteringDomains,
    EnteringKeywords,
    ChoosingAction
}

public class UserState
{
    public Stage Stage { get; set; }
    public List<string> Domains { get; set; } = [];
    public string Keywords { get; set; }
    public string PhotoCaption { get; set; }
    public string Action { get; set; }
    public int StartMessageId { get; set; }
    public string Username { get; set; }
}

public class Order
{
    public int OrderId { get; set; }
    public long ChatId { get; set; }
    public List<string> Domains { get; set; }
    public string Answer { get; set; }
    public AdminsMessage AdminsMessage { get; set; }
}

public class AdminsMessage
{
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public string ButtonText { get; set; }
    public bool ButtonTouched { get; set; }
}
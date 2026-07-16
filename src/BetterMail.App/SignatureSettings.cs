namespace BetterMail.App;

public sealed record SignaturePreference(string Id, string Name, string Html);

public sealed record MailboxSignaturePreferences(
    string? NewMailSignatureId = SignatureCatalog.DefaultId,
    string? ReplySignatureId = SignatureCatalog.DefaultId,
    string? ReplyAllSignatureId = SignatureCatalog.DefaultId,
    string? ForwardSignatureId = SignatureCatalog.DefaultId)
{
    public string? For(ComposeIntent intent) => intent switch
    {
        ComposeIntent.Reply => ReplySignatureId,
        ComposeIntent.ReplyAll => ReplyAllSignatureId,
        ComposeIntent.Forward => ForwardSignatureId,
        _ => NewMailSignatureId
    };
}

public sealed record SignatureContent(string Id, string Html);

public sealed record SignatureTemplate(
    string Id,
    string Name,
    string Description,
    string PreviewName,
    string PreviewDetail,
    string Accent,
    string Html);

public static class SignatureCatalog
{
    public const string DefaultId = "bettermail-default";

    public static SignaturePreference Default { get; } = new(
        DefaultId,
        "BetterMail Default",
        """
        <div style="margin-top:24px;padding-left:9px;border-left:3px solid #157efb;font:12px 'Segoe UI',Arial,sans-serif">Sent with <strong>BetterMail</strong></div>
        """);

    public static IReadOnlyList<SignatureTemplate> Templates { get; } =
    [
        new("blank", "Blank", "Start from scratch", "Your signature", "An empty canvas", "#157EFB", ""),
        new("minimal", "Minimal", "Clean name and contact details", "Alex Morgan", "Product · alex@example.com", "#157EFB", """
            <p style="font:14px 'Segoe UI',Arial,sans-serif;margin:18px 0 0"><strong>Alex Morgan</strong><br><span style="font-size:12px">Product · alex@example.com · +1 555 0100</span></p>
            """),
        new("professional", "Professional", "A polished business signature", "Alex Morgan", "Product Director · Northwind", "#0F6CBD", """
            <table cellpadding="0" cellspacing="0" style="font:13px 'Segoe UI',Arial,sans-serif;margin-top:20px"><tr><td style="border-left:4px solid #0f6cbd;padding-left:12px"><strong style="font-size:15px">Alex Morgan</strong><br><span>Product Director · Northwind</span><br><a href="mailto:alex@example.com">alex@example.com</a> · +1 555 0100<br><a href="https://example.com">example.com</a></td></tr></table>
            """),
        new("compact", "Compact", "Small enough for busy threads", "Alex Morgan · Product", "alex@example.com · +1 555 0100", "#008C7A", """
            <p style="font:12px 'Segoe UI',Arial,sans-serif;margin:14px 0 0"><strong>Alex Morgan</strong> · Product · <a href="mailto:alex@example.com">alex@example.com</a> · +1 555 0100</p>
            """),
        new("executive", "Executive", "Strong hierarchy and restrained detail", "Alex Morgan", "Chief Product Officer", "#040F2F", """
            <p style="font:13px Georgia,serif;margin:22px 0 0"><strong style="font-size:17px;color:#040f2f">Alex Morgan</strong><br><span style="font-style:italic">Chief Product Officer</span><br><span style="font-family:'Segoe UI',Arial,sans-serif;font-size:12px">Northwind · +1 555 0100 · <a href="mailto:alex@example.com">alex@example.com</a></span></p>
            """),
        new("modern", "Modern", "Bold accent with simple details", "ALEX MORGAN", "Product / Northwind", "#615FFF", """
            <div style="font:13px 'Segoe UI',Arial,sans-serif;margin-top:20px"><strong style="font-size:16px;color:#615fff">ALEX MORGAN</strong><br><span>Product / Northwind</span><br><span style="font-size:12px"><a href="mailto:alex@example.com">alex@example.com</a> · example.com</span></div>
            """),
        new("corporate", "Corporate", "Structured company contact block", "Alex Morgan", "Northwind Corporation", "#1F4E79", """
            <table cellpadding="0" cellspacing="0" style="font:12px Arial,sans-serif;margin-top:20px"><tr><td style="padding-right:14px;border-right:1px solid #c8c8c8"><strong style="font-size:15px;color:#1f4e79">NORTHWIND</strong></td><td style="padding-left:14px"><strong>Alex Morgan</strong><br>Product Director<br>+1 555 0100 · <a href="mailto:alex@example.com">alex@example.com</a></td></tr></table>
            """),
        new("support", "Customer Support", "Friendly support-team identity", "Alex · Customer Support", "We’re here to help", "#008C7A", """
            <div style="font:13px 'Segoe UI',Arial,sans-serif;margin-top:20px;padding:10px 12px;border-left:3px solid #008c7a"><strong>Alex · Customer Support</strong><br><span style="font-size:12px">We’re here to help · <a href="mailto:support@example.com">support@example.com</a><br>Support hours: Monday–Friday, 08:00–17:00</span></div>
            """),
        new("sales", "Sales", "Contact details with a clear next step", "Alex Morgan", "Book a conversation", "#D66A1F", """
            <div style="font:13px 'Segoe UI',Arial,sans-serif;margin-top:20px"><strong style="font-size:15px">Alex Morgan</strong><br>Account Executive · Northwind<br><a href="mailto:alex@example.com">alex@example.com</a> · +1 555 0100<br><a href="https://example.com/meet" style="color:#d66a1f"><strong>Book a conversation</strong></a></div>
            """),
        new("personal", "Personal", "Warm and informal", "Alex", "Thanks,", "#C84B71", """
            <p style="font:14px Georgia,serif;margin:20px 0 0">Thanks,<br><strong style="font-size:16px;color:#c84b71">Alex</strong><br><span style="font:12px 'Segoe UI',Arial,sans-serif"><a href="mailto:alex@example.com">alex@example.com</a></span></p>
            """),
        new("legal", "Legal / Disclaimer", "Contact block with disclaimer space", "Alex Morgan", "Legal · Northwind", "#666666", """
            <div style="font:12px Arial,sans-serif;margin-top:20px"><strong>Alex Morgan</strong><br>Legal · Northwind<br><a href="mailto:alex@example.com">alex@example.com</a><hr style="border:0;border-top:1px solid #d0d0d0"><span style="font-size:10px;color:#666">This message and any attachments may contain confidential information intended only for the named recipient.</span></div>
            """),
        new("social", "Social Links", "Contact details and social profiles", "Alex Morgan", "LinkedIn · Website", "#0A66C2", """
            <p style="font:13px 'Segoe UI',Arial,sans-serif;margin:20px 0 0"><strong style="font-size:15px">Alex Morgan</strong><br>Product · Northwind<br><a href="mailto:alex@example.com">Email</a> · <a href="https://linkedin.com">LinkedIn</a> · <a href="https://example.com">Website</a></p>
            """),
        new("logo", "Logo and Contact", "Designed for a company logo", "YOUR LOGO", "Alex Morgan · Product", "#157EFB", """
            <table cellpadding="0" cellspacing="0" style="font:12px 'Segoe UI',Arial,sans-serif;margin-top:20px"><tr><td style="background-color:#040f2f;color:#fff;padding:12px 16px"><strong>YOUR LOGO</strong></td><td style="padding-left:14px"><strong style="font-size:15px">Alex Morgan</strong><br>Product · Northwind<br><a href="mailto:alex@example.com">alex@example.com</a> · +1 555 0100</td></tr></table>
            """)
    ];
}

public sealed class SignatureItem(string id, string name, string html, bool isReadOnly = false) : ViewModelBase
{
    private string _name = name;
    private string _html = html;

    public string Id { get; } = id;
    public bool IsReadOnly { get; } = isReadOnly;
    public bool CanEdit => !IsReadOnly;

    public string Name
    {
        get => _name;
        internal set => SetProperty(ref _name, value);
    }

    public string Html
    {
        get => _html;
        internal set => SetProperty(ref _html, value);
    }

    public SignaturePreference ToPreference() => new(Id, Name, Html);
}

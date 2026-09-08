namespace BetterMail.Core;

public static class MailAddressList
{
    public static IReadOnlyList<MailAddress> Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var characters = value.ToCharArray();
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (escaped) { escaped = false; continue; }
            if (quoted && character == '\\') { escaped = true; continue; }
            if (character == '"') quoted = !quoted;
            if (!quoted && character == ';') characters[index] = ',';
        }
        var addresses = new System.Net.Mail.MailAddressCollection();
        var normalized = new string(characters).Trim().Trim(',').Trim();
        if (normalized.Length == 0) return [];
        addresses.Add(normalized);
        return addresses.Select(static address => new MailAddress(address.DisplayName, address.Address)).ToArray();
    }
}

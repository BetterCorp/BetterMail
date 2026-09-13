using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    public System.Windows.Input.ICommand ClearPeopleFilterCommand { get; private set; } = null!;
    public bool HasPeopleFilter => !string.IsNullOrWhiteSpace(ModuleSearchText);
    private async Task ClearPeopleFilterAsync()
    {
        ModuleSearchText = "";
        await OpenPeopleAsync();
    }
    public PersonEntry? EditingContact => _editingContact;
    private string? _defaultContactOwnerId;
    public string? DefaultContactOwnerId
    {
        get => _defaultContactOwnerId;
        set { if (SetProperty(ref _defaultContactOwnerId, value)) RaisePropertyChanged(nameof(DefaultContactOwner)); }
    }
    public ContactOwnerOption? DefaultContactOwner
    {
        get => ContactOwners.FirstOrDefault(owner => owner.Mailbox.Id == DefaultContactOwnerId) ?? ContactOwners.FirstOrDefault();
        set { if (value is not null) DefaultContactOwnerId = value.Mailbox.Id; }
    }
    private string _contactGivenName = "";
    public string ContactGivenName { get => _contactGivenName; set => SetProperty(ref _contactGivenName, value); }
    private string _contactSurname = "";
    public string ContactSurname { get => _contactSurname; set => SetProperty(ref _contactSurname, value); }
    private string _contactMobilePhone = "";
    public string ContactMobilePhone { get => _contactMobilePhone; set => SetProperty(ref _contactMobilePhone, value); }
    private string _contactCompanyName = "";
    public string ContactCompanyName { get => _contactCompanyName; set => SetProperty(ref _contactCompanyName, value); }
    private string _contactJobTitle = "";
    public string ContactJobTitle { get => _contactJobTitle; set => SetProperty(ref _contactJobTitle, value); }
    private string _contactOfficeLocation = "";
    public string ContactOfficeLocation { get => _contactOfficeLocation; set => SetProperty(ref _contactOfficeLocation, value); }
    private string _contactPersonalNotes = "";
    public string ContactPersonalNotes { get => _contactPersonalNotes; set => SetProperty(ref _contactPersonalNotes, value); }
    private string _contactBusinessPhones = "";
    public string ContactBusinessPhones { get => _contactBusinessPhones; set => SetProperty(ref _contactBusinessPhones, value); }
    private string _contactHomePhones = "";
    public string ContactHomePhones { get => _contactHomePhones; set => SetProperty(ref _contactHomePhones, value); }
    private void LoadContactDetails(ContactDetails? details)
    {
        ContactGivenName = details?.GivenName ?? "";
        ContactSurname = details?.Surname ?? "";
        ContactMobilePhone = details?.MobilePhone ?? "";
        ContactCompanyName = details?.CompanyName ?? "";
        ContactJobTitle = details?.JobTitle ?? "";
        ContactOfficeLocation = details?.OfficeLocation ?? "";
        ContactPersonalNotes = details?.PersonalNotes ?? "";
        ContactBusinessPhones = string.Join("; ", details?.BusinessPhones ?? []);
        ContactHomePhones = string.Join("; ", details?.HomePhones ?? []);
    }
    private ContactDetails EditedContactDetails()
    {
        var previous = _editingContact?.SavedContact?.Details;
        string? Changed(string value, string? old) => value == (old ?? "") ? null : value.Trim();
        IReadOnlyList<string>? Phones(string value, IReadOnlyList<string>? old)
        {
            var values = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return values.SequenceEqual(old ?? []) ? null : values;
        }
        return new(
            GivenName: Changed(ContactGivenName, previous?.GivenName),
            Surname: Changed(ContactSurname, previous?.Surname),
            MobilePhone: Changed(ContactMobilePhone, previous?.MobilePhone),
            CompanyName: Changed(ContactCompanyName, previous?.CompanyName),
            JobTitle: Changed(ContactJobTitle, previous?.JobTitle),
            OfficeLocation: Changed(ContactOfficeLocation, previous?.OfficeLocation),
            PersonalNotes: Changed(ContactPersonalNotes, previous?.PersonalNotes),
            BusinessPhones: Phones(ContactBusinessPhones, previous?.BusinessPhones),
            HomePhones: Phones(ContactHomePhones, previous?.HomePhones));
    }
}

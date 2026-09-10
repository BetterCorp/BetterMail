namespace BetterMail.App;

public sealed class SyncStep(string name) : ViewModelBase
{
    private string _detail = "Waiting";
    private bool _running;
    private double _progress;
    private bool _indeterminate = true;
    public string Name { get; } = name;
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public bool Running { get => _running; set => SetProperty(ref _running, value); }
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public bool Indeterminate { get => _indeterminate; set => SetProperty(ref _indeterminate, value); }
}

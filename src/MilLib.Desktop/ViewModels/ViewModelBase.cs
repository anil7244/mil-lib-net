using CommunityToolkit.Mvvm.ComponentModel;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The base every screen shares, and the one place loading failures are
/// caught.
///
/// Screens start loading from their constructor without awaiting it, which
/// means an exception has nowhere to go: the screen simply comes up empty and
/// the reason is lost. That happened during this build — a column that can be
/// null in the database but was not allowed to be in the model — and the only
/// symptom was a blank page. Now the failure is kept and shown.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private string _loadError = "";
    [ObservableProperty] private bool _isLoading;

    public bool HasLoadError => LoadError.Length > 0;

    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasLoadError));

    /// <summary>
    /// Runs a screen's load and keeps whatever went wrong.
    /// </summary>
    protected async Task GuardAsync(Func<Task> load)
    {
        IsLoading = true;
        LoadError = "";

        try
        {
            await load();
        }
        catch (Exception ex)
        {
            LoadError = $"This screen could not load its data. {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

using Common.WPF.ViewModels;
using RomForge.Core;

namespace RomForge.ViewModels.Settings;

public class PatchSettingsMainViewModel : ToolTabViewModel
{
    public PatchSettingsMainViewModel()
    {
        AppConfig.Instance.Patch.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PatchConfig.AutoCompress))
                OnPropertyChanged(nameof(AutoCompress));

            if (e.PropertyName == nameof(PatchConfig.OpenOutputFolderWhenCompleted))
                OnPropertyChanged(nameof(OpenOutputFolderWhenCompleted));
        };
    }

    public bool AutoCompress
    {
        get => AppConfig.Instance.Patch.AutoCompress;
        set
        {
            AppConfig.Instance.Patch.AutoCompress = value;
            OnPropertyChanged();
        }
    }

    public bool OpenOutputFolderWhenCompleted
    {
        get => AppConfig.Instance.Patch.OpenOutputFolderWhenCompleted;
        set
        {
            AppConfig.Instance.Patch.OpenOutputFolderWhenCompleted = value;
            OnPropertyChanged();
        }
    }
}

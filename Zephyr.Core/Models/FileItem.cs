using System.ComponentModel;

namespace Zephyr.Core.Models;

public enum ClipboardItemState { None, Cut, Copied }

public class FileItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public DateTime Created { get; set; }
    public string Extension { get; set; } = string.Empty;
    public FileAttributes Attributes { get; set; }

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    /// <summary>True when this folder is a locked root (drives the lock badge). Set during load.</summary>
    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked == value) return;
            _isLocked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLocked)));
        }
    }

    /// <summary>True when this locked folder has been unlocked for the current session (open padlock). Set during load.</summary>
    private bool _isUnlocked;
    public bool IsUnlocked
    {
        get => _isUnlocked;
        set
        {
            if (_isUnlocked == value) return;
            _isUnlocked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnlocked)));
        }
    }

    // Controlled by Settings.ShowFileExtensions; set statically so all items react together
    public static bool ShowExtensions { get; set; } = true;
    public string DisplayName
    {
        get
        {
            if (ShowExtensions || IsDirectory || string.IsNullOrEmpty(Extension)) return Name;
            var stripped = Name[..^Extension.Length];
            return stripped.Length > 0 ? stripped : Name;
        }
    }

    private bool _isRecentlyInteracted;
    public bool IsRecentlyInteracted
    {
        get => _isRecentlyInteracted;
        set
        {
            if (_isRecentlyInteracted == value) return;
            _isRecentlyInteracted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecentlyInteracted)));
        }
    }

    private DateTime? _recentInteractionTime;
    public DateTime? RecentInteractionTime
    {
        get => _recentInteractionTime;
        set
        {
            if (_recentInteractionTime == value) return;
            _recentInteractionTime = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecentInteractionTime)));
        }
    }

    public string AttributeDisplay
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            if ((Attributes & FileAttributes.ReadOnly) != 0) sb.Append('R');
            if ((Attributes & FileAttributes.Hidden)   != 0) sb.Append('H');
            if ((Attributes & FileAttributes.System)   != 0) sb.Append('S');
            if ((Attributes & FileAttributes.Archive)  != 0) sb.Append('A');
            return sb.ToString();
        }
    }

    private string? _contentSummary;
    public string ContentSummary
    {
        get => IsDirectory ? (_contentSummary ?? "…") : "";
        set
        {
            if (_contentSummary == value) return;
            _contentSummary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentSummary)));
        }
    }

    private long? _folderSize;
    public long? FolderSize
    {
        get => _folderSize;
        set
        {
            if (_folderSize == value) return;
            _folderSize = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FolderSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeDisplay)));
        }
    }

    private object? _thumbnailImage;
    public object? ThumbnailImage
    {
        get => _thumbnailImage;
        set
        {
            if (_thumbnailImage == value) return;
            _thumbnailImage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailImage)));
        }
    }

    private ClipboardItemState _clipboardState;
    public ClipboardItemState ClipboardState
    {
        get => _clipboardState;
        set
        {
            if (_clipboardState == value) return;
            _clipboardState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClipboardState)));
        }
    }

    private string _cloudBadge = string.Empty;
    public string CloudBadge
    {
        get => _cloudBadge;
        set
        {
            if (_cloudBadge == value) return;
            _cloudBadge = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloudBadge)));
        }
    }

    // Populated only when this item is a search result; shows the parent directory path
    public string SearchLocation { get; set; } = string.Empty;

    public string Icon => IsDirectory ? "" : "";
    public string SizeDisplay => IsDirectory
        ? (_folderSize.HasValue ? FormatSize(_folderSize.Value) : "")
        : FormatSize(Size);
    public string TypeDisplay => IsDirectory
        ? "Folder"
        : string.IsNullOrEmpty(Extension) ? "File" : $"{Extension.TrimStart('.').ToUpper()} File";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}

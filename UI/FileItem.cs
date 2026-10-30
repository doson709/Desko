using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Desko.UI
{
    internal sealed class FileItem : INotifyPropertyChanged
    {
        public FileItem(string path) => Path = path;

        public string Path { get; }
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Path);
        public bool IsFolder { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public int IconPx { get; set; }

        string _name = "";
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; Changed(); } }
        }

        ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set { if (!ReferenceEquals(_icon, value)) { _icon = value; Changed(); } }
        }

        double _iconSize = 48;
        public double IconSize
        {
            get => _iconSize;
            set
            {
                if (_iconSize == value) return;
                _iconSize = value;
                Changed();
                Changed(nameof(ItemWidth));
            }
        }

        public double ItemWidth => IconSize + 36;

        // In-place rename: the label turns into a text box while this is true.
        bool _isRenaming;
        public bool IsRenaming
        {
            get => _isRenaming;
            set { if (_isRenaming != value) { _isRenaming = value; Changed(); } }
        }

        string _editName = "";
        public string EditName
        {
            get => _editName;
            set { if (_editName != value) { _editName = value; Changed(); } }
        }

        // Highlighted while something is dragged onto this item (e.g. a folder about to receive files).
        bool _isDropTarget;
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set { if (_isDropTarget != value) { _isDropTarget = value; Changed(); } }
        }

        public override string ToString() => Name;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

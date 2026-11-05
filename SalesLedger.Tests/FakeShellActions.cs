using SalesLedger.Core.Services;

namespace SalesLedger.Tests
{
    /// <summary>Records what the app asked Windows to do, without launching anything.</summary>
    public sealed class FakeShellActions : IShellActions
    {
        public List<string> Opened { get; } = [];
        public List<string> Printed { get; } = [];
        public List<string> ShownInFolder { get; } = [];
        public List<string> FoldersOpened { get; } = [];
        public List<string> Copied { get; } = [];

        /// <summary>Simulates a default PDF app (like a browser) that has no print command.</summary>
        public bool PrintSupported { get; set; } = true;

        public void Open(string path) => Opened.Add(path);

        public bool Print(string path)
        {
            if (!PrintSupported) return false;
            Printed.Add(path);
            return true;
        }

        public void ShowInFolder(string path) => ShownInFolder.Add(path);
        public void OpenFolder(string folder) => FoldersOpened.Add(folder);

        public Task<bool> CopyFileToClipboard(string path)
        {
            Copied.Add(path);
            return Task.FromResult(true);
        }
    }
}
